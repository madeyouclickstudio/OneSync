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

    public RecycleBinService(
        GraphHttpClient graph,
        MetadataStore metadata,
        SyncQueue queue,
        IEnumerable<DriveConfig> drives,
        string? userPrincipalName,
        ILogger logger)
    {
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

        // Cancel any pending RemoteDelete — if it hasn't been sent yet,
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
            return RestoreFolder(drive, item);

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
            {
                _logger.Information("File in server recycle bin, opening web UI: {Drive}:{Path}",
                    item.DriveLetter, item.RelativePath);
                await OpenWebRecycleBinAsync(drive, ct);
                _metadata.RemoveDeleted(item.Key);
                return (true, null);
            }

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
