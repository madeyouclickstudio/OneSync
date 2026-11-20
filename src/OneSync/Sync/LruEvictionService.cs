using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.Shell;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Frees disk space when the drive holding the local copies runs low, by turning
/// the least recently used downloaded files back into placeholders. Only local
/// files are touched. A file is skipped if it has changes the cloud doesn't
/// have, has anything queued for it, was used recently, or is open elsewhere.
/// </summary>
internal sealed class LruEvictionService : IAsyncDisposable
{
    private readonly MetadataStore _metadata;
    private readonly SyncQueue _queue;
    private readonly LocalChangeSuppressor _suppressor;
    private readonly SyncSettings _settings;
    private readonly IEnumerable<DriveConfig> _drives;
    private readonly string _localStorageRoot;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Hold the suppressor open for this long AFTER truncation completes, so
    // FileSystemWatcher's debounce window has fully drained.
    private static readonly TimeSpan SuppressionGracePeriod = TimeSpan.FromSeconds(20);

    public event Action<string, long>? FileEvicted;       // (localPath, freedBytes)
    public event Action<long>? EvictionCycleCompleted;     // totalFreed in cycle

    public LruEvictionService(
        MetadataStore metadata, SyncQueue queue, LocalChangeSuppressor suppressor,
        SyncSettings settings, IEnumerable<DriveConfig> drives,
        string localStorageRoot, ILogger logger)
    {
        _metadata = metadata;
        _queue = queue;
        _suppressor = suppressor;
        _settings = settings;
        _drives = drives;
        _localStorageRoot = localStorageRoot;
        _logger = logger.ForContext("Component", "LruEviction");
    }

    public void Start()
    {
        if (!_settings.EvictionEnabled)
        {
            _logger.Information("LRU eviction disabled in config");
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.Information(
            "LRU eviction started: threshold={ThresholdGB}GB, target={TargetGB}GB, " +
            "interval={IntervalSec}s, minAge={MinAgeMin}min",
            _settings.EvictionFreeSpaceThresholdGB, _settings.EvictionTargetFreeSpaceGB,
            _settings.EvictionCheckIntervalSeconds, _settings.EvictionMinAgeMinutes);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Brief initial delay so the sync engine has time to populate
        // MetadataStore before we start checking what to evict.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                CheckAndEvict();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Eviction cycle threw - continuing");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(10, _settings.EvictionCheckIntervalSeconds)),
                    ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One eviction pass. Public for explicit triggering (e.g. tray menu).</summary>
    public void CheckAndEvict()
    {
        long freeBytes = GetFreeBytes();
        long thresholdBytes = (long)_settings.EvictionFreeSpaceThresholdGB * 1024 * 1024 * 1024;
        long targetBytes = (long)_settings.EvictionTargetFreeSpaceGB * 1024 * 1024 * 1024;

        if (freeBytes >= thresholdBytes)
        {
            _logger.Debug("Disk free {FreeGB:N1} GB >= threshold {ThresholdGB} GB - no eviction needed",
                freeBytes / 1024.0 / 1024.0 / 1024.0,
                _settings.EvictionFreeSpaceThresholdGB);
            return;
        }

        _logger.Information(
            "Disk free {FreeGB:N1} GB below threshold {ThresholdGB} GB - beginning eviction (target {TargetGB} GB)",
            freeBytes / 1024.0 / 1024.0 / 1024.0,
            _settings.EvictionFreeSpaceThresholdGB,
            _settings.EvictionTargetFreeSpaceGB);

        long totalFreed = 0;
        int evictedCount = 0;
        int skippedCount = 0;
        DateTime now = DateTime.UtcNow;
        DateTime minAgeCutoff = now.AddMinutes(-_settings.EvictionMinAgeMinutes);

        // Process drives in priority order - evict from lower-priority drives first.
        // (e.g., shared SharePoint libraries before personal OneDrive)
        var drivesByPriority = _drives.OrderByDescending(d => d.Priority).ToList();

        foreach (var drive in drivesByPriority)
        {
            if (freeBytes >= targetBytes) break;

            var candidates = _metadata.GetHydratedByLastAccessed(drive.ConfigId);
            foreach (var item in candidates)
            {
                if (freeBytes >= targetBytes) break;

                // LiteDB deserializes DateTime in the local timezone but
                // minAgeCutoff is built from DateTime.UtcNow. Convert to UTC
                // before comparing or we get a timezone-offset-sized bug.
                if (item.LastAccessedAt.ToUniversalTime() > minAgeCutoff)
                {
                    // All remaining candidates are even fresher (sorted ASC),
                    // so stop for this drive.
                    _logger.Debug("Reached the {MinAge}-minute fresh window for {Drive} - stopping eviction here",
                        _settings.EvictionMinAgeMinutes, drive.Letter);
                    break;
                }

                if (TryEvictOne(drive, item, out long freedBytes))
                {
                    freeBytes += freedBytes;
                    totalFreed += freedBytes;
                    evictedCount++;
                    try { FileEvicted?.Invoke(BuildLocalPath(drive, item.RelativePath), freedBytes); } catch { }
                }
                else
                {
                    skippedCount++;
                }
            }
        }

        _logger.Information(
            "Eviction cycle done: evicted {Count} files freeing {FreedMB:N1} MB ({SkippedCount} skipped). " +
            "New free: {FreeGB:N1} GB",
            evictedCount, totalFreed / 1024.0 / 1024.0, skippedCount,
            freeBytes / 1024.0 / 1024.0 / 1024.0);

        try { EvictionCycleCompleted?.Invoke(totalFreed); } catch { }
    }

    private bool TryEvictOne(DriveConfig drive, RemoteItem item, out long freedBytes)
    {
        freedBytes = 0;
        string localPath = BuildLocalPath(drive, item.RelativePath);

        // Safety check 1: pending uploads. If this item has any queued sync
        // operation, leave it alone - the upload will resolve and the file
        // becomes a candidate next cycle.
        if (_queue.HasPendingForPath(drive.ConfigId, item.RelativePath))
        {
            _logger.Debug("Skip {Path}: pending sync op queued", item.RelativePath);
            return false;
        }

        // Safety check 2: file present and not already empty.
        FileInfo? fi;
        try { fi = new FileInfo(localPath); }
        catch (Exception ex) { _logger.Debug(ex, "Skip {Path}: FileInfo threw", localPath); return false; }
        if (!fi.Exists || fi.Length == 0)
        {
            // Already dehydrated or never hydrated. Update metadata to match
            // disk reality so we don't keep retrying.
            if (item.Hydrated)
                _metadata.MarkDehydrated(drive.ConfigId, item.RelativePath);
            return false;
        }

        // Never evict anything the cloud doesn't have: local edits that haven't
        // been uploaded, or a copy whose cloud version changed underneath it.
        if (item.RemoteChangedSinceBaseline || LocalState.IsDirty(fi, item) || fi.Length != item.Size)
        {
            _logger.Debug("Skip {Path}: local copy differs from the cloud", item.RelativePath);
            return false;
        }

        // Engage the suppressor BEFORE we touch the file. The watcher's
        // debounce window is ~2s; we hold the suppressor for SuppressionGracePeriod
        // (20s) after the truncate completes to absorb stragglers.
        _suppressor.Suppress(localPath);

        long preSize = fi.Length;
        try
        {
            // Safety check 4: exclusive lock. We open with FileShare.None so
            // if Word / antivirus / preview pane has the file open, this
            // throws and we skip cleanly - no risk of mid-edit truncation.
            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // Truncate to 0.
                fs.SetLength(0);
                // Mark as sparse so the directory entry stays cheap.
                PlaceholderManager.SetSparse(fs.SafeFileHandle, true);
            }

            // Preserve timestamps so the file's modified date doesn't suddenly jump.
            try
            {
                if (item.LastModifiedDateTime != default)
                    File.SetLastWriteTimeUtc(localPath, item.LastModifiedDateTime);
                if (item.CreatedDateTime != default)
                    File.SetCreationTimeUtc(localPath, item.CreatedDateTime);
            }
            catch { /* timestamps are nice-to-have */ }

            // Update overlay icon: this file is now cloud-only.
            try { SyncStateMarker.Mark(localPath, SyncOverlayState.CloudOnly); } catch { }

            // Metadata only; nothing is sent to the cloud.
            _metadata.MarkDehydrated(drive.ConfigId, item.RelativePath);

            freedBytes = preSize;
            _logger.Information("Evicted {Path} ({SizeMB:N1} MB freed)",
                item.RelativePath, preSize / 1024.0 / 1024.0);

            // Schedule the suppressor release on a delay so any FileSystemWatcher
            // events from the truncate get dropped.
            _ = Task.Delay(SuppressionGracePeriod, CancellationToken.None)
                .ContinueWith(_ =>
                {
                    try { _suppressor.Release(localPath); } catch { }
                });

            return true;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            _logger.Debug("Skip {Path}: file is open in another process", item.RelativePath);
            // Release the suppressor immediately since we didn't actually
            // change the file.
            try { _suppressor.Release(localPath); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Eviction failed for {Path} - releasing suppressor and skipping",
                item.RelativePath);
            try { _suppressor.Release(localPath); } catch { }
            return false;
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        // HRESULT 0x80070020 = ERROR_SHARING_VIOLATION
        // HRESULT 0x80070021 = ERROR_LOCK_VIOLATION
        const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
        const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);
        return ex.HResult == ERROR_SHARING_VIOLATION || ex.HResult == ERROR_LOCK_VIOLATION;
    }

    private string BuildLocalPath(DriveConfig drive, string relativePath)
    {
        var winRel = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(drive.LocalRootPath, winRel);
    }

    private long GetFreeBytes()
    {
        try
        {
            var rootPath = Path.GetFullPath(_localStorageRoot);
            var pathRoot = Path.GetPathRoot(rootPath);
            if (string.IsNullOrEmpty(pathRoot)) return long.MaxValue;
            return new DriveInfo(pathRoot).AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "GetFreeBytes failed - returning MAX so we don't accidentally evict");
            return long.MaxValue;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop != null)
        {
            try { await _loop; } catch { }
        }
        _cts?.Dispose();
    }
}
