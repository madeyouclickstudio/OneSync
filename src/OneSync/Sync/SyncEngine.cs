using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Auth;
using OneSync.Config;
using OneSync.FileSystem;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Orchestrates: watchers per drive, upload worker, delta poller, placeholders,
/// hydration. Owns the lifecycle.
/// </summary>
internal sealed class SyncEngine : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly GraphAuthProvider _auth;
    private readonly QuotaCache _quotaCache;
    private readonly ILogger _logger;
    private readonly List<DriveConfig> _drives;

    private readonly SyncQueue _queue;
    private readonly MetadataStore _metadata;
    private readonly GraphHttpClient _graph;
    private readonly PlaceholderManager _placeholders;
    private readonly HydrationService _hydration;
    private readonly LocalChangeSuppressor _suppressor;
    private readonly ConflictResolver _conflictResolver;
    private readonly PendingManifestService _manifest;
    private readonly ThumbnailPrefetcher? _thumbnails;
    private readonly RecycleBinService _recycleBin;
    private readonly DeltaPoller _delta;
    private readonly LruEvictionService _eviction;
    private System.Threading.Timer? _manifestPushTimer;
    private readonly List<LocalWatcher> _watchers = new();
    private UploadWorker? _uploader;
    private Task? _uploaderTask;
    private CancellationTokenSource? _cts;

    public DeltaPoller DeltaPoller => _delta;
    public GraphHttpClient GraphHttp => _graph;
    public ConflictResolver Conflicts => _conflictResolver;
    public UploadWorker? Uploader => _uploader;
    public PendingManifestService Manifest => _manifest;
    public HydrationService Hydration => _hydration;
    public RecycleBinService RecycleBin => _recycleBin;
    public LruEvictionService Eviction => _eviction;

    /// <summary>Snapshot the local sync queue's pending uploads to manifest entries.</summary>
    public IEnumerable<PendingManifestEntry> SnapshotPendingForManifest()
    {
        var machine = Environment.MachineName;
        foreach (var op in _queue.GetPending(max: 10_000))
        {
            if (op.Type != SyncOpType.Upload) continue;
            yield return new PendingManifestEntry
            {
                Machine = machine,
                DriveLetter = op.DriveLetter,
                RelativePath = op.RelativePath,
                QueuedAtUtc = op.QueuedAt,
                SizeBytes = op.FileSizeBytes,
            };
        }
    }

    /// <summary>Pushes the current snapshot to the OneDrive manifest. Called on
    /// startup, periodically while running, and on flush completion.</summary>
    public async Task PushManifestAsync()
    {
        try
        {
            var entries = new List<PendingManifestEntry>(SnapshotPendingForManifest());
            await _manifest.UpdateThisMachineAsync(Environment.MachineName, entries);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Manifest push failed (non-critical)");
        }
    }

    /// <summary>
    /// Returns the local NTFS paths of every file that has a pending or retrying
    /// upload in the queue. Used by graceful shutdown to preserve those files
    /// from logoff cleanup so the next login can finish the upload.
    /// </summary>
    public IReadOnlyCollection<string> GetPendingUploadLocalPaths()
    {
        var result = new List<string>();
        var pending = _queue.GetPending(max: 10_000);
        foreach (var op in pending)
        {
            if (op.Type != SyncOpType.Upload) continue;
            var drive = _drives.FirstOrDefault(d => d.ConfigId == op.DriveConfigId);
            if (drive == null) continue;
            var winRel = Util.PathUtil.ToWindowsRelative(op.RelativePath);
            var localPath = System.IO.Path.Combine(drive.LocalRootPath, winRel);
            if (System.IO.File.Exists(localPath)) result.Add(localPath);
        }
        return result;
    }

    public SyncEngine(
        AppConfig config,
        GraphAuthProvider auth,
        QuotaCache quotaCache,
        IEnumerable<DriveConfig> drives,
        string queueDirectory,
        ILogger logger)
    {
        _config = config;
        _auth = auth;
        _quotaCache = quotaCache;
        _logger = logger;
        _drives = drives.ToList();

        Directory.CreateDirectory(queueDirectory);
        _queue = new SyncQueue(Path.Combine(queueDirectory, "sync_queue.db"), logger);
        _queue.ResetInProgressOnStartup();

        _metadata = new MetadataStore(Path.Combine(queueDirectory, "metadata.db"), logger);

        _graph = new GraphHttpClient(auth, config.SyncSettings, logger);

        // Drop stale ops: hydration temp files, ops for unknown drives, queued
        // uploads whose local file is gone/empty, and terminally-Failed ops for
        // files that match an exclude pattern (e.g. browser .crdownload/.part temp
        // files that should never have entered the queue and can never succeed).
        var excludePatterns = _config.SyncSettings.ExcludePatterns;
        var removed = _queue.RemoveStaleOperations(op =>
        {
            if (op.RelativePath.EndsWith(".hydrate.tmp", StringComparison.OrdinalIgnoreCase) ||
                (op.NewRelativePath?.EndsWith(".hydrate.tmp", StringComparison.OrdinalIgnoreCase) ?? false))
                return true;

            var drive = _drives.FirstOrDefault(d => d.ConfigId == op.DriveConfigId);
            if (drive == null) return true;

            if (op.Status == SyncOpStatus.Failed &&
                (Util.PathUtil.MatchesAnyGlob(op.RelativePath, excludePatterns) ||
                 (op.NewRelativePath != null && Util.PathUtil.MatchesAnyGlob(op.NewRelativePath, excludePatterns))))
                return true;

            if (op.Type == SyncOpType.Upload)
            {
                var winRel = Util.PathUtil.ToWindowsRelative(op.RelativePath);
                var localPath = Path.Combine(drive.LocalRootPath, winRel);
                return !File.Exists(localPath) || new FileInfo(localPath).Length == 0;
            }

            return false;
        });
        if (removed > 0)
            _logger.Information("Dropped {Removed} stale ops (hydration tmps, missing files, or excluded-file failures)", removed);
        _thumbnails = config.SyncSettings.EnableThumbnailPrefetch
            ? new ThumbnailPrefetcher(_graph, _drives,
                config.SyncSettings.MaxConcurrentThumbnailFetches, logger)
            : null;
        _placeholders = new PlaceholderManager(_metadata, logger, _thumbnails, _drives);
        _suppressor = new LocalChangeSuppressor();
        _hydration = new HydrationService(_graph, _metadata, _placeholders, _drives, _suppressor, config.SyncSettings, logger);
        _conflictResolver = new ConflictResolver(_metadata, _hydration, _queue, _suppressor, logger);
        _manifest = new PendingManifestService(_graph, logger);
        _recycleBin = new RecycleBinService(_graph, _metadata, _queue, _drives, _auth.Account?.Username, logger);
        _delta = new DeltaPoller(_graph, _metadata, _queue, _placeholders, _drives,
            _config.SyncSettings, _suppressor, logger);

        _eviction = new LruEvictionService(_metadata, _queue, _suppressor,
            _config.SyncSettings, _drives,
            Util.PathUtil.Expand(config.LocalStorageRoot ?? @"%LOCALAPPDATA%\OneSync\Drives"),
            logger);

        _logger.Information(
            "Sync engine initialised: queue pending {Pending}, failed {Failed}, metadata items {Meta}",
            _queue.CountPending(), _queue.CountFailed(),
            _drives.Sum(d => _metadata.CountFor(d.ConfigId)));
    }

    public SyncQueue Queue => _queue;
    public MetadataStore Metadata => _metadata;
    public IHydrationTrigger HydrationTrigger => _hydration;
    public LocalChangeSuppressor Suppressor => _suppressor;
    public PlaceholderManager Placeholders => _placeholders;

    public void ResetMetadata()
    {
        _metadata.ClearAll();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // If we have remembered metadata for drives whose local Drives folder
        // has been wiped (e.g. by logoff cleanup), recreate placeholders from
        // metadata before any watcher starts. Without this, the next delta
        // poll only fetches changes since the last token and won't recreate
        // the bulk of placeholders.
        RebuildMissingPlaceholders();

        foreach (var drive in _drives)
        {
            var watcher = new LocalWatcher(drive, _queue, _config.SyncSettings, _logger, _suppressor, _metadata);
            watcher.Start();
            _watchers.Add(watcher);
        }

        _uploader = new UploadWorker(_queue, _metadata, _graph, _drives, _quotaCache, _config.SyncSettings, _logger, _conflictResolver);
        _uploaderTask = Task.Run(() => _uploader.RunAsync(_cts.Token));

        _delta.Start();
        _eviction.Start();

        // Push manifest now + every 2 minutes so other machines see this
        // machine's pending uploads even if it never gets a chance to send a final
        // snapshot (e.g. abrupt power loss).
        _ = Task.Run(async () => { try { await PushManifestAsync(); } catch { } });
        _manifestPushTimer = new System.Threading.Timer(async _ =>
        {
            try { await PushManifestAsync(); } catch { /* logged */ }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

        _logger.Information(
            "SyncEngine started ({Watchers} watchers, upload worker running, delta poller polling every {Sec}s)",
            _watchers.Count, _config.SyncSettings.DeltaQueryIntervalSeconds);
    }

    private void RebuildMissingPlaceholders()
    {
        // Drives with folder redirection (e.g. H: with Desktop/Documents pointing into it)
        // need their placeholders eagerly rebuilt — Windows shell folders are visible
        // immediately at logon and showing them empty is jarring.
        //
        // Drives without folder redirection (shared SharePoint libraries — I:, J:) skip
        // the eager rebuild. Folders the user actually navigates to populate via
        // HydrationService's lazy-fallback path (cheap on-demand /children call). The
        // delta poller continues to add placeholders for items it sees in /delta. This
        // trades "everything ready upfront after a 2-3 hr rebuild for a 350k-item library"
        // for "folders fill in as you visit them, no startup cost" — much better fit for
        // libraries you only browse a corner of.
        var priorityDrives = _drives.Where(d => d.FolderRedirection?.Count > 0).ToList();
        foreach (var drive in priorityDrives)
            RebuildDrivePlaceholders(drive);

        var skipped = _drives.Where(d => d.FolderRedirection == null || d.FolderRedirection.Count == 0).ToList();
        if (skipped.Count > 0)
        {
            _logger.Information(
                "Skipping eager placeholder rebuild for {Count} background drives ({Drives}); lazy-fallback + delta poller will populate as needed",
                skipped.Count, string.Join(", ", skipped.Select(d => $"{d.Letter}:")));
        }
    }

    private void RebuildDrivePlaceholders(DriveConfig drive)
    {
        var metadataCount = _metadata.CountFor(drive.ConfigId);
        if (metadataCount == 0) return;

        var localCount = 0;
        try
        {
            if (Directory.Exists(drive.LocalRootPath))
                localCount = Directory.GetFileSystemEntries(drive.LocalRootPath).Length;
        }
        catch { /* ignore */ }

        if (localCount >= 2) return;

        _logger.Information(
            "Rebuilding placeholders for {Letter}: from {Count} metadata records (local appears wiped)",
            drive.Letter, metadataCount);

        _suppressor.Suppress(drive.LocalRootPath);
        try
        {
            int rebuilt = 0;
            foreach (var item in _metadata.GetForDrive(drive.ConfigId).Where(i => i.IsFolder))
            {
                item.Hydrated = false;
                _placeholders.CreateOrUpdate(drive, item);
                rebuilt++;
            }
            foreach (var item in _metadata.GetForDrive(drive.ConfigId).Where(i => !i.IsFolder))
            {
                item.Hydrated = false;
                _placeholders.CreateOrUpdate(drive, item);
                rebuilt++;
            }
            _logger.Information("Rebuilt {Count} placeholders for {Letter}:", rebuilt, drive.Letter);
        }
        finally
        {
            _ = Task.Delay(TimeSpan.FromSeconds(3))
                .ContinueWith(_ => _suppressor.Release(drive.LocalRootPath));
        }
    }

    public async Task StopAcceptingNewChangesAsync()
    {
        foreach (var w in _watchers)
        {
            try { w.Stop(); } catch (Exception ex) { _logger.Warning(ex, "Watcher stop failed"); }
        }
        await _delta.DisposeAsync();
    }

    public async Task FlushAndStopAsync(TimeSpan timeout)
    {
        await StopAcceptingNewChangesAsync();

        if (_uploader is null)
        {
            _cts?.Cancel();
            return;
        }

        var pending = _queue.CountPending();
        _logger.Information("Flush requested: {Pending} pending operations (timeout {Timeout})", pending, timeout);

        if (pending == 0)
        {
            _cts?.Cancel();
            return;
        }

        using var flushCts = new CancellationTokenSource(timeout);
        try
        {
            await _uploader.FlushAsync(flushCts.Token);
        }
        catch (OperationCanceledException)
        {
            var remaining = _queue.CountPending();
            _logger.Warning("Flush timed out after {Timeout} - {Remaining} ops remain in queue (will resume next session)",
                timeout, remaining);
        }
        catch (Exception ex) { _logger.Error(ex, "Flush threw"); }

        _cts?.Cancel();
        try { if (_uploaderTask != null) await _uploaderTask; }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex) { _logger.Warning(ex, "Uploader task ended with exception"); }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _manifestPushTimer?.Dispose(); } catch { }

        // Final manifest push so other machines see our up-to-date state
        try { await PushManifestAsync(); } catch { }

        if (_uploaderTask != null)
        {
            try { await _uploaderTask; } catch { }
        }
        await _delta.DisposeAsync();
        await _eviction.DisposeAsync();
        foreach (var w in _watchers)
        {
            try { w.Dispose(); } catch { }
        }
        _watchers.Clear();
        _hydration.Dispose();
        _thumbnails?.Dispose();
        _graph.Dispose();
        _metadata.Dispose();
        _queue.Dispose();
        _cts?.Dispose();
    }
}
