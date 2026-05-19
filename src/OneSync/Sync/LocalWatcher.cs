using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using OneSync.Config;
using OneSync.Util;
using Serilog;
using Timer = System.Threading.Timer;

namespace OneSync.Sync;

/// <summary>
/// Watches a single local drive root for filesystem changes, debounces rapid
/// repeats, applies exclusion filters, and enqueues sync operations.
/// </summary>
internal sealed class LocalWatcher : IDisposable
{
    private readonly DriveConfig _drive;
    private readonly SyncQueue _queue;
    private readonly SyncSettings _settings;
    private readonly ILogger _logger;
    private readonly LocalChangeSuppressor? _suppressor;
    private readonly MetadataStore? _metadata;
    private FileSystemWatcher? _fsw;
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new();
    private bool _disposed;

    public LocalWatcher(DriveConfig drive, SyncQueue queue, SyncSettings settings, ILogger logger,
        LocalChangeSuppressor? suppressor = null, MetadataStore? metadata = null)
    {
        _drive = drive;
        _queue = queue;
        _settings = settings;
        _logger = logger.ForContext("Drive", drive.Letter);
        _suppressor = suppressor;
        _metadata = metadata;
    }

    public void Start()
    {
        if (_fsw != null) return;

        Directory.CreateDirectory(_drive.LocalRootPath);

        _fsw = new FileSystemWatcher(_drive.LocalRootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            Filter = "*.*",
            InternalBufferSize = 65536,
        };
        _fsw.Created += OnCreated;
        _fsw.Changed += OnChanged;
        _fsw.Deleted += OnDeleted;
        _fsw.Renamed += OnRenamed;
        _fsw.Error += OnError;
        _fsw.EnableRaisingEvents = true;

        _logger.Information("LocalWatcher started for {Path}", _drive.LocalRootPath);
    }

    public void Stop()
    {
        if (_fsw is null) return;
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
        _fsw = null;

        foreach (var kvp in _debounceTimers)
            kvp.Value.Dispose();
        _debounceTimers.Clear();

        _logger.Information("LocalWatcher stopped for {Path}", _drive.LocalRootPath);
    }

    private bool ShouldExclude(string path)
    {
        if (_suppressor?.IsSuppressed(path) == true) return true;

        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;

        // Hydration temp files - never queue these as user changes
        if (name.EndsWith(".hydrate.tmp", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var pattern in _settings.ExcludePatterns)
        {
            if (PathUtil.MatchesGlob(name, pattern)) return true;
        }

        try
        {
            if (File.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Temporary) == FileAttributes.Temporary) return true;
            }
        }
        catch { /* ignore */ }

        if (path.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private string ToRelativePath(string fullPath)
    {
        var rel = Path.GetRelativePath(_drive.LocalRootPath, fullPath);
        rel = rel.Replace(Path.DirectorySeparatorChar, '/');
        if (!rel.StartsWith('/')) rel = "/" + rel;
        return rel;
    }

    private void Debounce(string path, Action action)
    {
        _debounceTimers.AddOrUpdate(path,
            _ => CreateDebounceTimer(path, action),
            (_, existing) =>
            {
                existing.Change(_settings.UploadDebounceMs, Timeout.Infinite);
                return existing;
            });
    }

    private Timer CreateDebounceTimer(string path, Action action)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            _debounceTimers.TryRemove(path, out var t);
            t?.Dispose();
            try { action(); }
            catch (Exception ex) { _logger.Warning(ex, "Debounced action failed for {Path}", path); }
        }, null, _settings.UploadDebounceMs, Timeout.Infinite);
        return timer;
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldExclude(e.FullPath)) return;

        // Tell Explorer immediately so the new file appears in any open
        // folder view without F5. The user perceives it as "saved into the
        // folder and it's just there" — which is how Explorer usually
        // behaves over native NTFS but doesn't for Dokan-mapped drives.
        try
        {
            var rel = ToRelativePath(e.FullPath);
            var isFolder = Directory.Exists(e.FullPath);
            ShellNotifier.NotifyCreated(_drive, rel, isFolder);
        }
        catch { }

        Debounce(e.FullPath, () => EnqueueUpload(e.FullPath, isNew: true));
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldExclude(e.FullPath)) return;
        // Filter out directory changes (we only care about file content)
        if (Directory.Exists(e.FullPath)) return;

        // Refresh Explorer's view (size column, modified date, etc.)
        try
        {
            var rel = ToRelativePath(e.FullPath);
            ShellNotifier.NotifyUpdated(_drive, rel);
        }
        catch { }

        Debounce(e.FullPath, () => EnqueueUpload(e.FullPath, isNew: false));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldExclude(e.FullPath)) return;
        var rel = ToRelativePath(e.FullPath);
        var capturedPath = e.FullPath;

        // Tell Explorer the entry is gone so it disappears from open views.
        try { ShellNotifier.NotifyDeleted(_drive, rel); } catch { }

        // Defensive: a delete event during hydration (file replaced) can race. Only
        // queue a remote delete if the file is STILL gone after a short grace period
        // AND was not suppressed.
        Debounce(capturedPath, () =>
        {
            if (_suppressor?.IsSuppressed(capturedPath) == true) return;
            if (File.Exists(capturedPath) || Directory.Exists(capturedPath))
            {
                _logger.Debug("Skipping remote delete - path reappeared (likely hydration race): {Path}", rel);
                return;
            }

            _queue.Enqueue(new SyncOperation
            {
                Type = SyncOpType.RemoteDelete,
                DriveConfigId = _drive.ConfigId,
                DriveLetter = _drive.Letter,
                RelativePath = rel,
                Priority = _drive.Priority,
            });
            _logger.Information("Queued remote delete: {Drive}:{Path}", _drive.Letter, rel);
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Ignore renames where source or target is a hydration tmp file - those are not user renames.
        if (ShouldExclude(e.FullPath) || ShouldExclude(e.OldFullPath)) return;

        // Tell Explorer about the rename so views update in place.
        try
        {
            var oldRelForShell = ToRelativePath(e.OldFullPath);
            var newRelForShell = ToRelativePath(e.FullPath);
            var isFolder = Directory.Exists(e.FullPath);
            ShellNotifier.NotifyRenamed(_drive, oldRelForShell, newRelForShell, isFolder);
        }
        catch { }

        // Also skip if the new name is in metadata (a synced file). Hydration moves a temp
        // file to a known remote path; we don't want to issue a remote rename in that case.
        var newRel = ToRelativePath(e.FullPath);
        var meta = _metadata?.Get(_drive.ConfigId, newRel);
        if (meta != null && !meta.IsFolder)
        {
            _logger.Debug("Skipping rename to known remote path (likely hydration): {Path}", newRel);
            return;
        }

        var oldRel = ToRelativePath(e.OldFullPath);
        _queue.Enqueue(new SyncOperation
        {
            Type = SyncOpType.RemoteRename,
            DriveConfigId = _drive.ConfigId,
            DriveLetter = _drive.Letter,
            RelativePath = oldRel,
            NewRelativePath = newRel,
            Priority = _drive.Priority,
        });
        _logger.Debug("Queued remote rename: {Drive}:{Old} -> {New}", _drive.Letter, oldRel, newRel);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.Error(e.GetException(), "FileSystemWatcher error on {Path} - restarting", _drive.LocalRootPath);
        try
        {
            Stop();
            Start();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not restart FileSystemWatcher");
        }
    }

    private void EnqueueUpload(string fullPath, bool isNew)
    {
        if (!File.Exists(fullPath)) return;
        var rel = ToRelativePath(fullPath);
        try
        {
            var fi = new FileInfo(fullPath);

            // Skip placeholders and hydration results - these are not user-originated changes.
            //
            // A file is a placeholder if metadata exists for it and the local size is 0.
            // A file is a hydration result if its local size exactly matches the remote size
            // and the metadata says it's already hydrated. In both cases the local content
            // is system-generated, not user-edited, and we should not push it back to the cloud.
            if (_metadata != null)
            {
                var meta = _metadata.Get(_drive.ConfigId, rel);
                if (meta != null && !meta.IsFolder)
                {
                    if (fi.Length == 0)
                    {
                        _logger.Debug("Skipping upload of placeholder (0 bytes): {Path}", rel);
                        return;
                    }
                    if (meta.Hydrated && fi.Length == meta.Size)
                    {
                        _logger.Debug("Skipping upload of hydrated file (matches remote): {Path}", rel);
                        return;
                    }
                }
            }

            if (fi.Length > _settings.MaxFileSizeMB * 1024L * 1024L)
            {
                _logger.Warning("Skipping {Path}: file size {Size} exceeds max {Max} MB",
                    rel, fi.Length, _settings.MaxFileSizeMB);
                return;
            }

            _queue.Enqueue(new SyncOperation
            {
                Type = SyncOpType.Upload,
                DriveConfigId = _drive.ConfigId,
                DriveLetter = _drive.Letter,
                RelativePath = rel,
                Priority = _drive.Priority,
                FileSizeBytes = fi.Length,
            });
            _logger.Information("Queued upload: {Drive}:{Path} ({Size} bytes, {Kind})",
                _drive.Letter, rel, fi.Length, isNew ? "new" : "changed");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not enqueue upload for {Path}", fullPath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
