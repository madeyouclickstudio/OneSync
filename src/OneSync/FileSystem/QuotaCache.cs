using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using OneSync.Config;
using OneSync.Util;
using Serilog;
using Timer = System.Threading.Timer;

namespace OneSync.FileSystem;

internal sealed class QuotaInfo
{
    [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("usedBytes")] public long UsedBytes { get; set; }
    [JsonPropertyName("remainingBytes")] public long RemainingBytes { get; set; }
    [JsonPropertyName("fetchedAt")] public DateTime FetchedAt { get; set; }
    [JsonPropertyName("driveLabel")] public string DriveLabel { get; set; } = string.Empty;

    public static QuotaInfo Empty() => new()
    {
        TotalBytes = 0,
        UsedBytes = 0,
        RemainingBytes = 0,
        FetchedAt = DateTime.MinValue,
        DriveLabel = string.Empty,
    };
}

internal sealed class QuotaCache : IDisposable
{
    private readonly GraphServiceClient _graph;
    private readonly ILogger _logger;
    private readonly string _cachePath;
    private readonly int _refreshIntervalSeconds;

    private readonly ConcurrentDictionary<string, QuotaInfo> _cache = new();
    private Timer? _refreshTimer;
    private readonly object _persistLock = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public QuotaCache(GraphServiceClient graph, string cacheDirectory, int refreshIntervalSeconds, ILogger logger)
    {
        _graph = graph;
        _logger = logger;
        _refreshIntervalSeconds = Math.Max(60, refreshIntervalSeconds);
        Directory.CreateDirectory(cacheDirectory);
        _cachePath = Path.Combine(cacheDirectory, "quota_cache.json");
        LoadFromDisk();
    }

    public QuotaInfo GetCached(DriveConfig drive)
    {
        if (_cache.TryGetValue(drive.ConfigId, out var info))
            return info;
        return QuotaInfo.Empty();
    }

    public async Task<QuotaInfo> RefreshAsync(DriveConfig drive, CancellationToken ct = default)
    {
        try
        {
            QuotaInfo info;
            if (drive.IsOneDrive)
                info = await FetchOneDriveAsync(drive, ct);
            else if (drive.IsSharePoint)
                info = await FetchSharePointAsync(drive, ct);
            else
                return QuotaInfo.Empty();

            info.DriveLabel = drive.Label;
            info.FetchedAt = DateTime.UtcNow;
            _cache[drive.ConfigId] = info;
            PersistToDisk();

            _logger.Information(
                "Quota refreshed for {Letter}: {Label} - {Used}/{Total} (free {Free})",
                drive.Letter, drive.Label, FormatBytes(info.UsedBytes),
                FormatBytes(info.TotalBytes), FormatBytes(info.RemainingBytes));

            return info;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Quota refresh failed for {Letter}: {Label} - keeping last known value",
                drive.Letter, drive.Label);
            return GetCached(drive);
        }
    }

    public async Task RefreshAllAsync(System.Collections.Generic.IEnumerable<DriveConfig> drives,
        CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            foreach (var d in drives)
            {
                if (ct.IsCancellationRequested) break;
                await RefreshAsync(d, ct);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void StartBackgroundRefresh(System.Collections.Generic.IEnumerable<DriveConfig> drives,
        CancellationToken ct)
    {
        var drivesList = drives.ToList();
        _refreshTimer = new Timer(async _ =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;
                await RefreshAllAsync(drivesList, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Background quota refresh failed");
            }
        }, null,
        TimeSpan.FromSeconds(_refreshIntervalSeconds),
        TimeSpan.FromSeconds(_refreshIntervalSeconds));
    }

    /// <summary>
    /// Marks the cached quota as stale and kicks off a background refresh. Until
    /// the refresh completes the previously known values remain in the cache so
    /// callers (e.g. Dokan GetDiskFreeSpace) don't fall back to local-disk space.
    /// </summary>
    public void Invalidate(DriveConfig drive)
    {
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(drive); }
            catch (Exception ex) { _logger.Debug(ex, "Background invalidate refresh failed"); }
        });
    }

    private async Task<QuotaInfo> FetchOneDriveAsync(DriveConfig drive, CancellationToken ct)
    {
        var od = await _graph.Me.Drive.GetAsync(cancellationToken: ct);
        var q = od?.Quota;
        return new QuotaInfo
        {
            TotalBytes = q?.Total ?? 0,
            UsedBytes = q?.Used ?? 0,
            RemainingBytes = q?.Remaining ?? 0,
        };
    }

    private async Task<QuotaInfo> FetchSharePointAsync(DriveConfig drive, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(drive.ResolvedDriveId))
            throw new InvalidOperationException(
                $"SharePoint drive {drive.Letter} has no ResolvedDriveId - permission check must run first");

        var d = await _graph.Drives[drive.ResolvedDriveId].GetAsync(cancellationToken: ct);
        var q = d?.Quota;
        var total = q?.Total ?? 0;
        var used = q?.Used ?? 0;
        var remaining = q?.Remaining ?? 0;

        if (total == 0 && !string.IsNullOrEmpty(drive.ResolvedSiteId))
        {
            try
            {
                var site = await _graph.Sites[drive.ResolvedSiteId].GetAsync(cancellationToken: ct);
                var siteUsed = site?.AdditionalData?.TryGetValue("storageUsed", out var storageObj) == true
                    ? Convert.ToInt64(storageObj) : 0;
                var siteTotal = site?.AdditionalData?.TryGetValue("storageQuota", out var quotaObj) == true
                    ? Convert.ToInt64(quotaObj) : 0;
                if (siteTotal > 0)
                {
                    total = siteTotal;
                    used = siteUsed;
                    remaining = siteTotal - siteUsed;
                }
            }
            catch { }
        }

        if (total == 0)
        {
            total = 1L * 1024 * 1024 * 1024 * 1024; // 1 TB default for SharePoint
            remaining = total - used;
        }

        return new QuotaInfo
        {
            TotalBytes = total,
            UsedBytes = used,
            RemainingBytes = remaining,
        };
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, QuotaInfo>>(json);
            if (data is null) return;
            foreach (var kvp in data)
                _cache[kvp.Key] = kvp.Value;
            _logger.Information("Loaded {Count} cached quota entries from disk", data.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not load quota cache from {Path} - starting fresh", _cachePath);
        }
    }

    private void PersistToDisk()
    {
        lock (_persistLock)
        {
            try
            {
                var snapshot = _cache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                var tmp = _cachePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_cachePath)) File.Delete(_cachePath);
                File.Move(tmp, _cachePath);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not persist quota cache to {Path}", _cachePath);
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        var u = 0;
        double v = bytes;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }

    /// <summary>Snapshot of all cached quotas. Used by diagnostic export.</summary>
    public IReadOnlyList<object> SnapshotAll()
    {
        var snap = new System.Collections.Generic.List<object>();
        foreach (var kvp in _cache)
        {
            snap.Add(new
            {
                key = kvp.Key,
                drive_label = kvp.Value.DriveLabel,
                total_bytes = kvp.Value.TotalBytes,
                used_bytes = kvp.Value.UsedBytes,
                remaining_bytes = kvp.Value.RemainingBytes,
                fetched_utc = kvp.Value.FetchedAt,
            });
        }
        return snap;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _refreshLock?.Dispose();
    }
}
