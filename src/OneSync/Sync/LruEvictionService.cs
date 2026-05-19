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
/// LRU local-disk eviction. When free space on the volume hosting
/// <see cref="AppConfig.LocalStorageRoot"/> drops below
/// <see cref="SyncSettings.EvictionFreeSpaceThresholdGB"/>, this service
/// truncates the least-recently-used hydrated files back to 0-byte
/// placeholders. The cloud copy is NEVER touched.
///
/// Critical safety invariants (do not change without re-validating every one):
///
/// 1. ZERO REMOTE OPERATIONS. This class holds no reference to GraphHttpClient,
///    UploadWorker, or anything that talks to Graph. The only outbound effect
///    of an eviction is `File.Open(... FileMode.Truncate ...)` on a local file
///    and a `_metadata.MarkDehydrated(...)` call.
///
/// 2. SUPPRESS LOCAL WATCHER. Before truncating a file, the local-change
///    suppressor is engaged for that path. Without this, the FileSystemWatcher
///    would see a "Changed" event with size=0 and enqueue an upload that
///    would overwrite the cloud copy with empty content. The suppressor is
///    held for SuppressionGracePeriod AFTER eviction completes to swallow
///    any debounced watcher events.
///
/// 3. SKIP PENDING UPLOADS. If the SyncQueue has any pending operation for
///    this path, we don't evict — uploading is the wrong moment to throw
///    away the local copy. Modified files always upload first.
///
/// 4. SKIP RECENTLY-ACCESSED. Files touched within
///    <see cref="SyncSettings.EvictionMinAgeMinutes"/> are off-limits even
///    when disk is full — protects the file the user is actively working with.
///
/// 5. EXCLUSIVE LOCK CHECK. The file is opened with FileShare.None during
///    truncation. If anything else has it open (Word, antivirus, Explorer
///    preview), the eviction silently skips and tries another victim.
///
/// 6. IDEMPOTENCE. Re-evicting an already-evicted file is a no-op (file is
///    already 0 bytes and metadata says Hydrated=false).
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
                _logger.Warning(ex, "Eviction cycle threw — continuing");
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
            _logger.Debug("Disk free {FreeGB:N1} GB >= threshold {ThresholdGB} GB — no eviction needed",
                freeBytes / 1024.0 / 1024.0 / 1024.0,
                _settings.EvictionFreeSpaceThresholdGB);
            return;
        }

        _logger.Information(
            "Disk free {FreeGB:N1} GB below threshold {ThresholdGB} GB — beginning eviction (target {TargetGB} GB)",
            freeBytes / 1024.0 / 1024.0 / 1024.0,
            _settings.EvictionFreeSpaceThresholdGB,
            _settings.EvictionTargetFreeSpaceGB);

        long totalFreed = 0;
        int evictedCount = 0;
        int skippedCount = 0;
        DateTime now = DateTime.UtcNow;
        DateTime minAgeCutoff = now.AddMinutes(-_settings.EvictionMinAgeMinutes);

        // Process drives in priority order — evict from lower-priority drives first.
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
                    _logger.Debug("Reached the {MinAge}-minute fresh window for {Drive} — stopping eviction here",
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
        // operation, leave it alone — the upload will resolve and the file
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

        // Safety check 3: size sanity. If the local file is smaller than the
        // recorded remote Size, the file is in some inconsistent state — DO
        // NOT touch it. The next sync cycle will reconcile.
        if (fi.Length < item.Size)
        {
            _logger.Debug("Skip {Path}: local size {Local} < remote size {Remote} (inconsistent)",
                item.RelativePath, fi.Length, item.Size);
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
            // throws and we skip cleanly — no risk of mid-edit truncation.
            using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // Truncate to 0.
                fs.SetLength(0);
                // Mark as sparse so the directory entry stays cheap.
                MarkSparse(fs.SafeFileHandle.DangerousGetHandle());
            }

            // Preserve timestamps so the file's modified date doesn't suddenly jump.
            try
            {
                if (item.LastModifiedDateTime != default)
                    File.SetLastWriteTime(localPath, item.LastModifiedDateTime);
                if (item.CreatedDateTime != default)
                    File.SetCreationTime(localPath, item.CreatedDateTime);
            }
            catch { /* timestamps are nice-to-have */ }

            // Update overlay icon: this file is now cloud-only.
            try { SyncStateMarker.Mark(localPath, SyncOverlayState.CloudOnly); } catch { }

            // Flip the metadata flag. CRITICAL: this is the ONLY metadata mutation —
            // it does NOT enqueue any remote operation.
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
            _logger.Warning(ex, "Eviction failed for {Path} — releasing suppressor and skipping",
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
            _logger.Debug(ex, "GetFreeBytes failed — returning MAX so we don't accidentally evict");
            return long.MaxValue;
        }
    }

    private static void MarkSparse(IntPtr handle)
    {
        try
        {
            DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0,
                out _, IntPtr.Zero);
        }
        catch { /* best effort */ }
    }

    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

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
