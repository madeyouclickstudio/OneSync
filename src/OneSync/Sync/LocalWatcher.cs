using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using OneSync.Config;
using OneSync.Util;
using Serilog;
using Timer = System.Threading.Timer;

namespace OneSync.Sync;

/// <summary>
/// Watches one drive's local folder and queues the changes the user makes.
/// Events for a path are debounced; when the path settles we look at what is
/// actually on disk and queue whatever brings the cloud in line with it. That
/// way a burst of events (an Office save is a create, two renames and a delete)
/// always ends in the right result, whatever order the events arrived in.
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
    private readonly ConcurrentDictionary<string, Timer> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _settleLock = new();
    private bool _disposed;

    /// <summary>Raised when the watcher's buffer overflowed and events were lost,
    /// so a full scan is needed to catch up.</summary>
    public event Action<DriveConfig>? EventsLost;

    public DriveConfig Drive => _drive;

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
            Filter = "*",
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

    /// <summary>Stops watching. Changes still waiting out their debounce are
    /// queued now rather than dropped.</summary>
    public void Stop()
    {
        if (_fsw is null) return;
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
        _fsw = null;

        foreach (var path in _pending.Keys)
        {
            if (_pending.TryRemove(path, out var timer))
            {
                timer.Dispose();
                Settle(path);
            }
        }

        _logger.Information("LocalWatcher stopped for {Path}", _drive.LocalRootPath);
    }

    /// <summary>Names and places that are never synced (Office lock and temp
    /// files, the recycle bin and so on).</summary>
    internal static bool IsIgnoredName(string path, SyncSettings settings)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var pattern in settings.ExcludePatterns)
            if (PathUtil.MatchesGlob(name, pattern)) return true;

        return path.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsIgnored(string path)
    {
        if (IsIgnoredName(path, _settings)) return true;
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Temporary) != 0) return true;
        }
        catch { }
        return false;
    }

    private bool IsOwnWrite(string path) => _suppressor?.IsSuppressed(path) == true;

    private string ToRelativePath(string fullPath) =>
        PathUtil.NormalizeRelative(Path.GetRelativePath(_drive.LocalRootPath, fullPath));

    private void Schedule(string path)
    {
        _pending.AddOrUpdate(path,
            p => new Timer(_ => Fire(p), null, _settings.UploadDebounceMs, Timeout.Infinite),
            (_, existing) =>
            {
                existing.Change(_settings.UploadDebounceMs, Timeout.Infinite);
                return existing;
            });
    }

    /// <summary>A change reported by the drive itself rather than the folder watcher.</summary>
    public void NotifyChanged(string fullPath)
    {
        if (IsOwnWrite(fullPath) || IsIgnoredName(fullPath, _settings)) return;
        Schedule(fullPath);
    }

    private void Fire(string path)
    {
        if (_pending.TryRemove(path, out var timer)) timer.Dispose();
        Settle(path);
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (IsOwnWrite(e.FullPath) || IsIgnored(e.FullPath)) return;
        try { ShellNotifier.NotifyCreated(_drive, ToRelativePath(e.FullPath), Directory.Exists(e.FullPath)); }
        catch { }
        Schedule(e.FullPath);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsOwnWrite(e.FullPath) || IsIgnored(e.FullPath)) return;
        if (Directory.Exists(e.FullPath)) return;
        try { ShellNotifier.NotifyUpdated(_drive, ToRelativePath(e.FullPath)); }
        catch { }
        Schedule(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsOwnWrite(e.FullPath) || IsIgnoredName(e.FullPath, _settings)) return;
        try { ShellNotifier.NotifyDeleted(_drive, ToRelativePath(e.FullPath)); }
        catch { }
        Schedule(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsOwnWrite(e.FullPath) && IsOwnWrite(e.OldFullPath)) return;

        bool oldIgnored = IsIgnoredName(e.OldFullPath, _settings);
        bool newIgnored = IsIgnored(e.FullPath);

        try
        {
            ShellNotifier.NotifyRenamed(_drive, ToRelativePath(e.OldFullPath), ToRelativePath(e.FullPath),
                Directory.Exists(e.FullPath));
        }
        catch { }

        if (oldIgnored && newIgnored) return;

        if (oldIgnored || newIgnored)
        {
            // Half of a save: an app moving the real file aside, or moving its temp
            // file into place. Either way, settle whichever real name was involved.
            Schedule(oldIgnored ? e.FullPath : e.OldFullPath);
            return;
        }

        lock (_settleLock)
            QueueRename(e.OldFullPath, e.FullPath);
    }

    private void QueueRename(string oldFull, string newFull)
    {
        var oldRel = ToRelativePath(oldFull);
        var newRel = ToRelativePath(newFull);
        var meta = _metadata?.Get(_drive.ConfigId, oldRel);

        if (meta is null || string.IsNullOrEmpty(meta.RemoteItemId))
        {
            // Never reached the cloud under the old name: just send what's there now.
            _queue.CancelWaitingUploads(_drive.ConfigId, oldRel);
            Schedule(newFull);
            return;
        }

        // Move our record with the file so the new name isn't mistaken for a new,
        // unsynced file, then rename the cloud item.
        _metadata!.MovePath(_drive.ConfigId, oldRel, newRel);
        _queue.Enqueue(new SyncOperation
        {
            Type = SyncOpType.RemoteRename,
            DriveConfigId = _drive.ConfigId,
            DriveLetter = _drive.Letter,
            RelativePath = oldRel,
            NewRelativePath = newRel,
            RemoteItemId = meta.RemoteItemId,
            Priority = _drive.Priority,
        });
        _logger.Information("Queued remote rename: {Drive}:{Old} -> {New}", _drive.Letter, oldRel, newRel);

        // The old name may have been the target of a debounced save; that save
        // now belongs to the new name.
        if (_pending.TryRemove(oldFull, out var t))
        {
            t.Dispose();
            Schedule(newFull);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.Error(e.GetException(), "FileSystemWatcher error on {Path} - restarting", _drive.LocalRootPath);
        try
        {
            if (_fsw != null)
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Dispose();
                _fsw = null;
            }
            Start();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not restart FileSystemWatcher");
        }
        try { EventsLost?.Invoke(_drive); } catch { }
    }

    /// <summary>Looks at a path that has stopped changing and queues whatever the
    /// cloud needs to match it.</summary>
    private void Settle(string fullPath)
    {
        try
        {
            lock (_settleLock)
            {
                if (IsOwnWrite(fullPath)) return;
                var rel = ToRelativePath(fullPath);

                if (Directory.Exists(fullPath))
                {
                    var meta = _metadata?.Get(_drive.ConfigId, rel);
                    if (meta is null || !meta.IsFolder)
                        QueueNewTree(fullPath, rel);
                    return;
                }

                if (File.Exists(fullPath))
                {
                    if (IsIgnored(fullPath)) return;
                    QueueUploadIfChanged(fullPath, rel);
                    return;
                }

                QueueDeleteIfKnown(rel);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not queue change for {Path}", fullPath);
        }
    }

    /// <summary>A folder we have no record of (made, copied or moved in): create
    /// it and send anything inside it, since its contents may not raise events
    /// of their own.</summary>
    private void QueueNewTree(string fullPath, string rel)
    {
        QueueFolder(rel);
        foreach (var entry in new DirectoryInfo(fullPath).EnumerateFileSystemInfos())
        {
            if (IsIgnored(entry.FullName)) continue;
            var childRel = rel.TrimEnd('/') + "/" + entry.Name;
            if (entry is DirectoryInfo)
            {
                var meta = _metadata?.Get(_drive.ConfigId, childRel);
                if (meta is null || !meta.IsFolder) QueueNewTree(entry.FullName, childRel);
            }
            else
            {
                QueueUploadIfChanged(entry.FullName, childRel);
            }
        }
    }

    private void QueueFolder(string rel)
    {
        _queue.Enqueue(new SyncOperation
        {
            Type = SyncOpType.RemoteCreateFolder,
            DriveConfigId = _drive.ConfigId,
            DriveLetter = _drive.Letter,
            RelativePath = rel,
            Priority = _drive.Priority,
        });
        _logger.Information("Queued folder create: {Drive}:{Path}", _drive.Letter, rel);
    }

    private void QueueUploadIfChanged(string fullPath, string rel)
    {
        var fi = new FileInfo(fullPath);
        var meta = _metadata?.Get(_drive.ConfigId, rel);

        if (!LocalState.IsDirty(fi, meta) && meta?.RemoteChangedSinceBaseline != true)
            return;

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
            _drive.Letter, rel, fi.Length, meta is null ? "new" : "changed");
    }

    private void QueueDeleteIfKnown(string rel)
    {
        // Part of a folder that was deleted as a whole: the folder's own delete covers it.
        var parent = PathUtil.ParentOf(rel);
        if (parent != "/" && !Directory.Exists(PlaceholderManager.LocalPathFor(_drive, parent)) &&
            _metadata?.Get(_drive.ConfigId, parent) is { IsFolder: true })
        {
            _queue.CancelWaitingUploads(_drive.ConfigId, rel);
            return;
        }

        var meta = _metadata?.Get(_drive.ConfigId, rel);
        bool waiting = _queue.CancelWaitingUploads(_drive.ConfigId, rel) > 0;
        if (meta is null)
        {
            if (waiting)
                _logger.Information("Dropped queued upload of {Path}: deleted before it was sent", rel);
            return;
        }

        _queue.Enqueue(new SyncOperation
        {
            Type = SyncOpType.RemoteDelete,
            DriveConfigId = _drive.ConfigId,
            DriveLetter = _drive.Letter,
            RelativePath = rel,
            RemoteItemId = string.IsNullOrEmpty(meta.RemoteItemId) ? null : meta.RemoteItemId,
            Priority = _drive.Priority,
        });
        _metadata!.AddDeleted(new DeletedItem
        {
            RemoteItemId = meta.RemoteItemId,
            DriveConfigId = _drive.ConfigId,
            DriveLetter = _drive.Letter,
            RelativePath = rel,
            Name = meta.Name,
            IsFolder = meta.IsFolder,
            Size = meta.Size,
            DeletedAtUtc = DateTime.UtcNow,
            LastModifiedDateTime = meta.LastModifiedDateTime,
        });
        _metadata.DeleteTree(_drive.ConfigId, rel);
        _logger.Information("Queued remote delete: {Drive}:{Path}", _drive.Letter, rel);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
