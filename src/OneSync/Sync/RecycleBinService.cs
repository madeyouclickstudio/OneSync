using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

internal sealed class RecycleBinService
{
    private readonly GraphHttpClient _graph;
    private readonly MetadataStore _metadata;
    private readonly SyncQueue _queue;
    private readonly IReadOnlyList<DriveConfig> _drives;
    private readonly ILogger _logger;
    private readonly string? _userPrincipalName;
    private readonly ConcurrentDictionary<string, string> _driveWebUrls = new();

    public event Action<DeletedItem>? ItemRestored;

    /// <summary>The item is being restored in the cloud (by us, or by the user on
    /// the web recycle bin page): watch for it coming back.</summary>
    public event Action<DriveConfig, DeletedItem>? RestoringInCloud;

    private readonly Func<string, CancellationToken, Task<string?>>? _sharePointToken;
    private static readonly HttpClient s_sharePoint = new() { Timeout = TimeSpan.FromSeconds(30) };

    public RecycleBinService(
        GraphHttpClient graph,
        MetadataStore metadata,
        SyncQueue queue,
        IEnumerable<DriveConfig> drives,
        string? userPrincipalName,
        ILogger logger,
        Func<string, CancellationToken, Task<string?>>? sharePointToken = null)
    {
        _sharePointToken = sharePointToken;
        _graph = graph;
        _metadata = metadata;
        _queue = queue;
        _drives = drives.ToList();
        _userPrincipalName = userPrincipalName;
        _logger = logger.ForContext("Component", "RecycleBin");
    }

    public List<DeletedItem> GetRecentlyDeleted(int max = 50) =>
        _metadata.GetRecentlyDeleted(max);

    public async Task<(bool Success, string? Error)> RestoreAsync(
        DeletedItem item, CancellationToken ct = default)
    {
        var drive = _drives.FirstOrDefault(d => d.ConfigId == item.DriveConfigId);
        if (drive == null)
            return (false, "Drive no longer configured");

        if (string.IsNullOrEmpty(item.RemoteItemId))
            return (false, "No remote item ID");

        // Cancel any pending RemoteDelete - if it hasn't been sent yet,
        // the file still exists on the server and we just re-download it.
        int cancelled = _queue.RemoveStaleOperations(op =>
            op.Type == SyncOpType.RemoteDelete &&
            op.DriveConfigId == item.DriveConfigId &&
            string.Equals(op.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            (op.Status == SyncOpStatus.Pending || op.Status == SyncOpStatus.Retry));

        if (cancelled > 0)
            _logger.Information("Cancelled {Count} pending remote-delete for {Drive}:{Path}",
                cancelled, item.DriveLetter, item.RelativePath);

        if (item.IsFolder)
        {
            // Gone from the cloud: bring it back from the cloud recycle bin with its
            // contents. Making the folder here would only upload an empty one.
            if (cancelled == 0 && await IsGoneFromCloudAsync(drive, item, ct))
                return await RestoreFromCloudBinAsync(drive, item, ct);
            return RestoreFolder(drive, item);
        }

        return await RestoreFileAsync(drive, item, ct);
    }

    private async Task<(bool Success, string? Error)> RestoreFileAsync(
        DriveConfig drive, DeletedItem item, CancellationToken ct)
    {
        _logger.Information("Restoring: {Drive}:{Path} (id={Id})",
            item.DriveLetter, item.RelativePath, item.RemoteItemId);

        string downloadUrl = drive.IsOneDrive
            ? $"https://graph.microsoft.com/v1.0/me/drive/items/{item.RemoteItemId}/content"
            : $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/items/{item.RemoteItemId}/content";

        byte[] content;
        try
        {
            using var dlResp = await _graph.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, downloadUrl),
                ct, HttpCompletionOption.ResponseHeadersRead);

            if ((int)dlResp.StatusCode == 404)
                return await RestoreFromCloudBinAsync(drive, item, ct);

            if (!dlResp.IsSuccessStatusCode)
            {
                var body = await dlResp.Content.ReadAsStringAsync(ct);
                _logger.Warning("Restore download failed {Status} for {Drive}:{Path}: {Body}",
                    (int)dlResp.StatusCode, item.DriveLetter, item.RelativePath, Truncate(body, 300));
                return (false, $"Download failed (HTTP {(int)dlResp.StatusCode})");
            }

            content = await dlResp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Restore download threw for {Drive}:{Path}",
                item.DriveLetter, item.RelativePath);
            return (false, "Download failed");
        }

        var winRel = item.RelativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var localPath = Path.Combine(drive.LocalRootPath, winRel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, content, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Restore local write failed for {Path}", localPath);
        }

        _metadata.RemoveDeleted(item.Key);
        _logger.Information("Restored: {Drive}:{Path} ({Size} bytes)",
            item.DriveLetter, item.RelativePath, content.Length);
        ItemRestored?.Invoke(item);
        return (true, null);
    }

    private (bool Success, string? Error) RestoreFolder(DriveConfig drive, DeletedItem item)
    {
        _logger.Information("Restoring folder: {Drive}:{Path}", item.DriveLetter, item.RelativePath);

        var winRel = item.RelativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var localPath = Path.Combine(drive.LocalRootPath, winRel);
        try
        {
            Directory.CreateDirectory(localPath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Restore folder create failed for {Path}", localPath);
            return (false, "Could not create folder");
        }

        _metadata.RemoveDeleted(item.Key);
        _logger.Information("Restored folder: {Drive}:{Path}", item.DriveLetter, item.RelativePath);
        ItemRestored?.Invoke(item);
        return (true, null);
    }

    private static string ItemUrl(DriveConfig drive, string remoteItemId) => drive.IsOneDrive
        ? $"https://graph.microsoft.com/v1.0/me/drive/items/{remoteItemId}?$select=id"
        : $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/items/{remoteItemId}?$select=id";

    private async Task<bool> IsGoneFromCloudAsync(DriveConfig drive, DeletedItem item, CancellationToken ct)
    {
        try
        {
            using var resp = await _graph.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, ItemUrl(drive, item.RemoteItemId)), ct);
            return resp.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch { return false; }
    }

    /// <summary>
    /// The item is in the cloud recycle bin. Graph can only restore from a personal
    /// OneDrive's bin, so this uses SharePoint's REST API; without the permission
    /// for that, the web recycle bin opens for the user to restore it there. Either
    /// way the drive is then watched closely until the item is back.
    /// </summary>
    private async Task<(bool Success, string? Error)> RestoreFromCloudBinAsync(
        DriveConfig drive, DeletedItem item, CancellationToken ct)
    {
        bool restored = false;
        try { restored = await TryRestoreWithSharePointAsync(drive, item, ct); }
        catch (Exception ex) { _logger.Warning(ex, "Restore from the cloud recycle bin failed for {Path}", item.RelativePath); }

        if (!restored)
        {
            _logger.Information("In the cloud recycle bin, opening the web page: {Drive}:{Path}",
                item.DriveLetter, item.RelativePath);
            await OpenWebRecycleBinAsync(drive, ct);
        }
        _metadata.RemoveDeleted(item.Key);
        try { RestoringInCloud?.Invoke(drive, item); } catch { }
        return (true, null);
    }

    /// <summary>
    /// Finds the item in the site's recycle bin and restores it. A file deleted
    /// along with its folder is only in the bin as part of that folder, so failing
    /// an entry for the item itself, the nearest deleted folder above it is restored.
    /// </summary>
    private async Task<bool> TryRestoreWithSharePointAsync(DriveConfig drive, DeletedItem item, CancellationToken ct)
    {
        if (_sharePointToken is null) return false;
        var (web, library) = await GetWebAndLibraryAsync(drive, ct);
        if (web is null || library is null) return false;

        var token = await _sharePointToken(new Uri(web).Host, ct);
        if (token is null) return false;

        var parts = item.RelativePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int n = parts.Length; n >= 1; n--)
        {
            var leaf = parts[n - 1];
            var dir = library + (n > 1 ? "/" + string.Join("/", parts.Take(n - 1)) : "");
            var id = await FindInRecycleBinAsync(web, token, leaf, dir, ct);
            if (id is null) continue;

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{web}/_api/web/RecycleBin('{id}')/restore()");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json;odata=nometadata");
            using var resp = await s_sharePoint.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                _logger.Information("Restored from the cloud recycle bin: {Drive}:/{Path}{Note}", item.DriveLetter,
                    string.Join("/", parts.Take(n)),
                    n < parts.Length ? " (the folder it was deleted with)" : "");
                return true;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.Warning("Cloud recycle bin restore of {Leaf} failed {Status}: {Body}",
                leaf, (int)resp.StatusCode, Truncate(body, 300));
            return false;
        }
        _logger.Information("{Path} not found in the cloud recycle bin", item.RelativePath);
        return false;
    }

    /// <summary>Id of the newest recycle bin entry named <paramref name="leaf"/> that was in
    /// <paramref name="dir"/> (server-relative, no leading slash), or null.</summary>
    private static async Task<string?> FindInRecycleBinAsync(string web, string token, string leaf, string dir,
        CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"LeafName eq '{leaf.Replace("'", "''")}'");
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{web}/_api/web/RecycleBin?$filter={filter}&$select=Id,LeafName,DirName,DeletedDate");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json;odata=nometadata");
        using var resp = await s_sharePoint.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("value", out var value)) return null;
        string? best = null;
        DateTime bestAt = DateTime.MinValue;
        foreach (var e in value.EnumerateArray())
        {
            var dirName = e.TryGetProperty("DirName", out var d) ? d.GetString()?.Trim('/') : null;
            if (!string.Equals(dirName, dir.Trim('/'), StringComparison.OrdinalIgnoreCase)) continue;
            var at = e.TryGetProperty("DeletedDate", out var t) && t.TryGetDateTime(out var dt) ? dt : DateTime.MinValue;
            if (best is null || at > bestAt)
            {
                best = e.GetProperty("Id").GetString();
                bestAt = at;
            }
        }
        return best;
    }

    /// <summary>The site (web) URL and the library's server-relative path for a drive.</summary>
    private async Task<(string? Web, string? Library)> GetWebAndLibraryAsync(DriveConfig drive, CancellationToken ct)
    {
        string? libraryUrl;
        string? web;
        if (drive.IsOneDrive)
        {
            using var resp = await _graph.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive?$select=webUrl"), ct);
            if (!resp.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            libraryUrl = doc.RootElement.TryGetProperty("webUrl", out var w) ? w.GetString() : null;
            if (string.IsNullOrEmpty(libraryUrl)) return (null, null);
            var trimmed = libraryUrl.TrimEnd('/');
            web = trimmed[..trimmed.LastIndexOf('/')];
        }
        else
        {
            if (string.IsNullOrEmpty(drive.SiteUrl)) return (null, null);
            web = drive.SiteUrl.TrimEnd('/');
            libraryUrl = drive.ResolvedLibraryWebUrl;
            if (string.IsNullOrEmpty(libraryUrl)) return (null, null);
        }
        var library = Uri.UnescapeDataString(new Uri(libraryUrl).AbsolutePath).Trim('/');
        return (web, library);
    }

    private async Task OpenWebRecycleBinAsync(DriveConfig drive, CancellationToken ct)
    {
        string? url = null;
        if (drive.IsSharePoint && !string.IsNullOrEmpty(drive.SiteUrl))
        {
            url = drive.SiteUrl.TrimEnd('/') + "/_layouts/15/RecycleBin.aspx";
        }
        else if (drive.IsOneDrive)
        {
            url = await GetOneDriveRecycleBinUrlAsync(drive, ct);
        }

        if (url != null)
        {
            _logger.Information("Opening recycle bin URL: {Url}", url);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to open recycle bin URL"); }
        }
    }

    private async Task<string?> GetOneDriveRecycleBinUrlAsync(DriveConfig drive, CancellationToken ct)
    {
        var cacheKey = drive.ConfigId;
        if (_driveWebUrls.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            using var resp = await _graph.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get,
                    "https://graph.microsoft.com/v1.0/me/drive?$select=webUrl"),
                ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("webUrl", out var webUrlProp)) return null;

            var webUrl = webUrlProp.GetString();
            if (string.IsNullOrEmpty(webUrl)) return null;

            // webUrl is e.g. "https://tenant-my.sharepoint.com/personal/user_domain_com/Documents"
            // Strip the last path segment and append recycle bin
            var uri = new Uri(webUrl);
            var basePath = uri.AbsolutePath.TrimEnd('/');
            var lastSlash = basePath.LastIndexOf('/');
            if (lastSlash > 0)
                basePath = basePath[..lastSlash];

            var recycleBinUrl = $"{uri.Scheme}://{uri.Host}{basePath}/_layouts/15/RecycleBin.aspx";
            _driveWebUrls.TryAdd(cacheKey, recycleBinUrl);
            return recycleBinUrl;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to resolve OneDrive recycle bin URL from Graph");
            return null;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
