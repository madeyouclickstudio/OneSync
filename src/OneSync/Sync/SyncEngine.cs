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
    private System.Threading.Timer? _reconcileTimer;
    private int _reconciling;
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
    /// Local paths that must survive logoff cleanup: anything still waiting to
    /// go up, and any file or folder the cloud doesn't have as it is here.
    /// </summary>
    public IReadOnlyCollection<string> GetPendingUploadLocalPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in _queue.GetOutstanding())
        {
            if (op.Type != SyncOpType.Upload && op.Type != SyncOpType.RemoteCreateFolder) continue;
            var drive = _drives.FirstOrDefault(d => d.ConfigId == op.DriveConfigId);
            if (drive == null) continue;
            var localPath = PlaceholderManager.LocalPathFor(drive, op.RelativePath);
            if (File.Exists(localPath) || Directory.Exists(localPath)) result.Add(localPath);
        }
        foreach (var drive in _drives)
        {
            foreach (var (fullPath, _, _) in FindUnsyncedLocal(drive))
                result.Add(fullPath);
        }
        return result;
    }

    /// <summary>Hands a change seen by the drive itself to that drive's watcher.</summary>
    public void NotifyLocalChange(DriveConfig drive, string fullPath)
    {
        var watcher = _watchers.FirstOrDefault(w => w.Drive.ConfigId == drive.ConfigId);
        watcher?.NotifyChanged(fullPath);
    }

    /// <summary>
    /// Walks a drive's local folder for anything the cloud doesn't have yet:
    /// files edited or created locally, and folders with no cloud record.
    /// </summary>
    private IEnumerable<(string fullPath, string relativePath, bool isFolder)> FindUnsyncedLocal(DriveConfig drive)
    {
        var root = drive.LocalRootPath;
        if (!Directory.Exists(root)) yield break;

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                if (LocalWatcher.IsIgnoredName(entry.FullName, _config.SyncSettings)) continue;
                var rel = PathUtil.NormalizeRelative(Path.GetRelativePath(root, entry.FullName));
                if (rel.Equals("/" + OneSyncDokanFS.ProtectedFolderName, StringComparison.OrdinalIgnoreCase)) continue;

                var meta = _metadata.Get(drive.ConfigId, rel);
                if (entry is DirectoryInfo)
                {
                    if (meta is null || !meta.IsFolder) yield return (entry.FullName, rel, true);
                    stack.Push(entry.FullName);
                    continue;
                }

                var fi = (FileInfo)entry;
                if ((fi.Attributes & FileAttributes.Temporary) != 0) continue;
                if (meta?.RemoteChangedSinceBaseline == true || LocalState.IsDirty(fi, meta))
                    yield return (entry.FullName, rel, false);
            }
        }
    }

    /// <summary>
    /// Queues anything on disk that the cloud is missing. A backstop for changes
    /// the watcher never reported: a buffer overflow, changes made while the app
    /// was closing, or a crash before a debounced change was queued.
    /// </summary>
    public int ReconcileLocalChanges(DriveConfig? only = null)
    {
        if (Interlocked.Exchange(ref _reconciling, 1) == 1) return 0;
        int queued = 0;
        try
        {
            var outstanding = new HashSet<string>(
                _queue.GetOutstanding().Select(o => o.DriveConfigId + "|" + o.RelativePath),
                StringComparer.OrdinalIgnoreCase);
            var maxBytes = _config.SyncSettings.MaxFileSizeMB * 1024L * 1024L;

            foreach (var drive in _drives)
            {
                if (only != null && drive.ConfigId != only.ConfigId) continue;
                foreach (var (fullPath, rel, isFolder) in FindUnsyncedLocal(drive))
                {
                    if (outstanding.Contains(drive.ConfigId + "|" + rel)) continue;
                    if (_suppressor.IsSuppressed(fullPath)) continue;

                    long size = 0;
                    if (!isFolder)
                    {
                        try { size = new FileInfo(fullPath).Length; } catch { continue; }
                        if (size > maxBytes) continue;
                    }

                    _queue.Enqueue(new SyncOperation
                    {
                        Type = isFolder ? SyncOpType.RemoteCreateFolder : SyncOpType.Upload,
                        DriveConfigId = drive.ConfigId,
                        DriveLetter = drive.Letter,
                        RelativePath = rel,
                        Priority = drive.Priority,
                        FileSizeBytes = size,
                    });
                    queued++;
                    _logger.Information("Found unsynced local {Kind}: {Drive}:{Path}",
                        isFolder ? "folder" : "file", drive.Letter, rel);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Local reconcile failed");
        }
        finally
        {
            Volatile.Write(ref _reconciling, 0);
        }
        if (queued > 0)
            _logger.Information("Local reconcile queued {Count} changes", queued);
        return queued;
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

        // Drop ops that can never succeed: old hydration temp files, unknown
        // drives, uploads whose file has gone, and failures for excluded names.
        var excludePatterns = _config.SyncSettings.ExcludePatterns;
        var removed = _queue.RemoveStaleOperations(op =>
        {
            if (LocalState.IsDownloadTemp(op.RelativePath) ||
                (op.NewRelativePath != null && LocalState.IsDownloadTemp(op.NewRelativePath)))
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
                return !File.Exists(localPath);
            }

            return false;
        });
        if (removed > 0)
            _logger.Information("Dropped {Removed} stale ops (hydration tmps, missing files, or excluded-file failures)", removed);
        _thumbnails = config.SyncSettings.EnableThumbnailPrefetch
            ? new ThumbnailPrefetcher(_graph, _drives,
                config.SyncSettings.MaxConcurrentThumbnailFetches, logger)
            : null;
        _suppressor = new LocalChangeSuppressor();
        _placeholders = new PlaceholderManager(_metadata, _suppressor, logger, _thumbnails);
        _hydration = new HydrationService(_graph, _metadata, _placeholders, _drives, _suppressor, config.SyncSettings, logger);
        _conflictResolver = new ConflictResolver(_metadata, _placeholders, _queue, _suppressor, logger);
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

    /// <summary>Forgets everything learned from the cloud except the records of
    /// files held locally, so the next listings rebuild the rest.</summary>
    public void ResetMetadata()
    {
        _metadata.PruneToLocalCopies(keepFolders: true);
        _metadata.ClearAllEnumerationState();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        TidyLocalFiles();

        // Put back placeholders that logoff cleanup removed, before the watchers start.
        RebuildMissingPlaceholders();

        foreach (var drive in _drives)
        {
            var watcher = new LocalWatcher(drive, _queue, _config.SyncSettings, _logger, _suppressor, _metadata);
            watcher.EventsLost += d => Task.Run(() => ReconcileLocalChanges(d));
            watcher.Start();
            _watchers.Add(watcher);
        }

        _uploader = new UploadWorker(_queue, _metadata, _graph, _drives, _quotaCache, _config.SyncSettings, _logger, _conflictResolver);
        _uploaderTask = Task.Run(() => _uploader.RunAsync(_cts.Token));

        _delta.Start();
        _eviction.Start();

        _reconcileTimer = new System.Threading.Timer(_ => ReconcileLocalChanges(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(10));

        // Publish this PC's pending uploads now and every 2 minutes, so other PCs
        // still see them if this one loses power.
        _ = Task.Run(async () => { try { await PushManifestAsync(); } catch { } });
        _manifestPushTimer = new System.Threading.Timer(async _ =>
        {
            try { await PushManifestAsync(); } catch { /* logged */ }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

        _logger.Information(
            "SyncEngine started ({Watchers} watchers, upload worker running, delta poller polling every {Sec}s)",
            _watchers.Count, _config.SyncSettings.DeltaQueryIntervalSeconds);
    }

    /// <summary>
    /// Before the watchers start: deletes downloads left half done by an earlier
    /// run, and puts right files older versions left in a confusing state.
    /// Sparse files with content are made ordinary files and kept; empty files
    /// standing in for placeholders get the sparse flag back.
    /// </summary>
    private void TidyLocalFiles()
    {
        DateTime started;
        try { started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { started = DateTime.UtcNow; }

        foreach (var drive in _drives)
        {
            if (!Directory.Exists(drive.LocalRootPath)) continue;
            int temps = 0, kept = 0, restored = 0;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(drive.LocalRootPath, "*", SearchOption.AllDirectories); }
            catch (Exception ex) { _logger.Warning(ex, "Could not scan {Drive}: for leftover files", drive.Letter); continue; }

            foreach (var path in files)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (LocalState.IsDownloadTemp(path))
                    {
                        if (fi.LastWriteTimeUtc < started) { fi.Delete(); temps++; }
                        continue;
                    }

                    bool sparse = (fi.Attributes & FileAttributes.SparseFile) != 0;
                    if (!(sparse && fi.Length > 0) && !(!sparse && fi.Length == 0)) continue;

                    var rel = PathUtil.NormalizeRelative(Path.GetRelativePath(drive.LocalRootPath, path));
                    lock (_metadata.ApplyLock)
                    {
                        var meta = _metadata.Get(drive.ConfigId, rel);
                        if (sparse)
                        {
                            // Content written into a placeholder, or a download from an
                            // older version. Either way it may be someone's work.
                            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                                PlaceholderManager.SetSparse(fs.SafeFileHandle, false);
                            if (meta is { IsFolder: false } && !LocalState.HasBaseline(meta))
                            {
                                meta.Hydrated = true;
                                meta.RemoteChangedSinceBaseline = true;
                                _metadata.Upsert(meta);
                            }
                            kept++;
                        }
                        else if (LocalState.IsOldStylePlaceholder(fi, meta))
                        {
                            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                                PlaceholderManager.SetSparse(fs.SafeFileHandle, true);
                            restored++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not tidy {Path}", path);
                }
            }

            if (temps + kept + restored > 0)
                _logger.Information(
                    "Tidied {Drive}: {Temps} leftover download(s) deleted, {Kept} file(s) with unsynced content kept, {Restored} placeholder(s) restored",
                    drive.Letter, temps, kept, restored);
        }
    }

    private void RebuildMissingPlaceholders()
    {
        // Drives holding redirected folders (Desktop, Documents) are rebuilt straight
        // away, as they're on screen at logon. Other drives fill in folder by folder
        // as they're opened.
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

        int rebuilt = 0;
        foreach (var item in _metadata.GetForDrive(drive.ConfigId).OrderByDescending(i => i.IsFolder))
        {
            try { _placeholders.CreateOrUpdate(drive, item); rebuilt++; }
            catch (Exception ex) { _logger.Debug(ex, "Rebuild failed for {Path}", item.RelativePath); }
        }
        _logger.Information("Rebuilt {Count} placeholders for {Letter}:", rebuilt, drive.Letter);
    }

    public async Task StopAcceptingNewChangesAsync()
    {
        try { _reconcileTimer?.Dispose(); } catch { }
        foreach (var w in _watchers)
        {
            try { w.Stop(); } catch (Exception ex) { _logger.Warning(ex, "Watcher stop failed"); }
        }
        await _delta.DisposeAsync();
        ReconcileLocalChanges();
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
        try { _reconcileTimer?.Dispose(); } catch { }

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
