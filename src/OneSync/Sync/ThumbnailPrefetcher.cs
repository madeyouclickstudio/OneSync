using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Downloads Microsoft Graph thumbnails for placeholder image files and stores
/// them as NTFS alternate data streams on the placeholder (filename:OneSyncThumb).
/// Our native shell-extension DLL reads the ADS and returns the bitmap to
/// Explorer, so users see real previews on files they haven't opened yet.
///
/// Cost: ~30 KB per image file fetched (vs multi-MB for the actual photo).
/// Files we don't support (Office docs etc.) just show a generic icon
/// until the user opens them, at which point hydration converts the file
/// to real NTFS and the OS handler takes over.
/// </summary>
internal sealed class ThumbnailPrefetcher : IDisposable
{
    public const string AdsName = "OneSyncThumb";

    // Conservative: only types where the IThumbnailProvider in our DLL is
    // registered. Office/PDF rely on their own handlers post-hydration.
    private static readonly HashSet<string> ThumbnailableExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp",
            ".heic", ".heif",
        };

    private readonly GraphHttpClient _graph;
    private readonly Dictionary<string, DriveConfig> _drivesById;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public ThumbnailPrefetcher(
        GraphHttpClient graph,
        IEnumerable<DriveConfig> drives,
        int maxConcurrentFetches,
        ILogger logger)
    {
        _graph = graph;
        _drivesById = new Dictionary<string, DriveConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drives) _drivesById[d.ConfigId] = d;
        _logger = logger.ForContext("Component", "ThumbnailPrefetcher");
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrentFetches),
                                  Math.Max(1, maxConcurrentFetches));
    }

    public static bool IsThumbnailableExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ThumbnailableExtensions.Contains(ext);
    }

    /// <summary>
    /// Queues a fetch for a placeholder file. Safe to call multiple times for
    /// the same path - duplicate requests collapse. Fire-and-forget; failures
    /// are silent (the file just keeps its generic icon).
    /// </summary>
    public void EnqueueIfMissing(DriveConfig drive, RemoteItem item, string localPath)
    {
        if (!IsThumbnailableExtension(localPath)) return;
        if (string.IsNullOrEmpty(item.RemoteItemId)) return;
        if (HasThumbnailAds(localPath)) return; // already prefetched

        if (!_inFlight.TryAdd(localPath, 0)) return; // already queued

        _ = Task.Run(async () =>
        {
            try { await FetchAsync(drive, item, localPath, CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Thumbnail fetch failed for {Path}", item.RelativePath);
            }
            finally { _inFlight.TryRemove(localPath, out _); }
        });
    }

    private async Task FetchAsync(DriveConfig drive, RemoteItem item, string localPath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Re-check after the gate - another instance may have completed
            if (HasThumbnailAds(localPath)) return;

            var url = BuildThumbnailsListUrl(drive, item.RemoteItemId);
            using var listResp = await _graph.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (!listResp.IsSuccessStatusCode)
            {
                _logger.Debug("Thumbnail list {Status} for {Path}",
                    (int)listResp.StatusCode, item.RelativePath);
                return;
            }

            var json = await listResp.Content.ReadAsStringAsync(ct);
            var thumbUrl = ExtractMediumThumbUrl(json);
            if (thumbUrl == null)
            {
                _logger.Debug("No thumbnail available from Graph for {Path}", item.RelativePath);
                return;
            }

            // The thumbnail URL is a pre-signed CDN URL - don't attach auth
            var capturedThumbUrl = thumbUrl;
            using var getResp = await _graph.SendPreSignedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, capturedThumbUrl), ct);
            if (!getResp.IsSuccessStatusCode)
            {
                _logger.Debug("Thumbnail download {Status} for {Path}",
                    (int)getResp.StatusCode, item.RelativePath);
                return;
            }

            var bytes = await getResp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return;

            WriteThumbnailAds(localPath, bytes);
            _logger.Debug("Cached {Size} byte thumbnail for {Path}", bytes.Length, item.RelativePath);
        }
        finally { _gate.Release(); }
    }

    private static bool HasThumbnailAds(string localPath)
    {
        try
        {
            using var fs = new FileStream($"{localPath}:{AdsName}", FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.Length > 0;
        }
        catch { return false; }
    }

    private void WriteThumbnailAds(string localPath, byte[] bytes)
    {
        if (!File.Exists(localPath)) return;
        try
        {
            using var fs = new FileStream($"{localPath}:{AdsName}", FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(bytes, 0, bytes.Length);
        }
        catch (IOException ex)
        {
            _logger.Debug(ex, "Could not write thumbnail ADS for {Path}", localPath);
        }
    }

    private static string BuildThumbnailsListUrl(DriveConfig drive, string itemId)
    {
        if (drive.IsOneDrive)
            return $"https://graph.microsoft.com/v1.0/me/drive/items/{itemId}/thumbnails?$select=medium,large";
        return $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/items/{itemId}/thumbnails?$select=medium,large";
    }

    /// <summary>Parses the thumbnails-list JSON and returns the URL of the
    /// best available preview (large preferred, falling back to medium).</summary>
    private static string? ExtractMediumThumbUrl(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("value", out var arr) ||
                arr.GetArrayLength() == 0) return null;
            var first = arr[0];
            // Prefer large (~800x800) for a sharp Explorer thumbnail, fall back to medium.
            if (first.TryGetProperty("large", out var large) &&
                large.TryGetProperty("url", out var lurl))
                return lurl.GetString();
            if (first.TryGetProperty("medium", out var medium) &&
                medium.TryGetProperty("url", out var murl))
                return murl.GetString();
            if (first.TryGetProperty("small", out var small) &&
                small.TryGetProperty("url", out var surl))
                return surl.GetString();
        }
        catch { }
        return null;
    }

    public void Dispose() => _gate?.Dispose();
}
