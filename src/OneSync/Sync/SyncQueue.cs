using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Persistent sync operation queue backed by LiteDB. Thread-safe via LiteDB's own locking.
/// </summary>
internal sealed class SyncQueue : IDisposable
{
    private const string CollectionName = "sync_ops";
    private const string DeltaCollectionName = "delta_tokens";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SyncOperation> _ops;
    private readonly ILiteCollection<DeltaToken> _deltaTokens;
    private readonly ILogger _logger;
    private readonly object _writeLock = new();

    public string DatabasePath { get; }

    public SyncQueue(string databasePath, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        DatabasePath = databasePath;
        _logger = logger;

        var connectionString = new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Direct,
        };
        _db = new LiteDatabase(connectionString);
        _ops = _db.GetCollection<SyncOperation>(CollectionName);
        _ops.EnsureIndex(o => o.Status);
        _ops.EnsureIndex(o => o.Priority);
        _ops.EnsureIndex(o => o.QueuedAt);
        _ops.EnsureIndex(o => o.DriveConfigId);
        _ops.EnsureIndex(o => o.RelativePath);
        _ops.EnsureIndex(o => o.Type);

        _deltaTokens = _db.GetCollection<DeltaToken>(DeltaCollectionName);
        _deltaTokens.EnsureIndex(t => t.DriveConfigId, unique: true);
    }

    public void Enqueue(SyncOperation op)
    {
        lock (_writeLock)
        {
            var waiting = _ops.Find(o =>
                    o.DriveConfigId == op.DriveConfigId &&
                    (o.Status == SyncOpStatus.Pending || o.Status == SyncOpStatus.Retry))
                .ToList();

            switch (op.Type)
            {
                case SyncOpType.Upload:
                {
                    // One waiting upload per path; a newer save just refreshes it.
                    var existing = waiting.FirstOrDefault(o =>
                        o.Type == SyncOpType.Upload && SamePath(o.RelativePath, op.RelativePath));
                    if (existing != null)
                    {
                        existing.FileSizeBytes = op.FileSizeBytes;
                        existing.ContentHash = op.ContentHash;
                        // Behind anything queued since, such as a rename of this path.
                        existing.QueuedAt = DateTime.UtcNow;
                        existing.Status = SyncOpStatus.Pending;
                        existing.NextAttemptAt = null;
                        existing.ErrorMessage = null;
                        _ops.Update(existing);
                        return;
                    }

                    // The file was deleted here and is back under the same name before
                    // the delete reached the cloud (a fresh download over an old one, an
                    // app that saves by delete and recreate). Send it as a new version of
                    // the cloud file it would have deleted, not a delete and a new file:
                    // a new item at the same URL leaves Word holding the old one, and the
                    // user sees the old content under "a newer version is on the server".
                    var delete = waiting.FirstOrDefault(o =>
                        o.Type == SyncOpType.RemoteDelete && !o.FollowsUpload &&
                        !string.IsNullOrEmpty(o.ETag) && !string.IsNullOrEmpty(o.RemoteItemId) &&
                        SamePath(o.RelativePath, op.RelativePath));
                    if (delete != null)
                    {
                        _ops.Delete(delete.Id);
                        op.ETag = delete.ETag;
                        op.RemoteItemId = delete.RemoteItemId;
                        op.ReplacesDeleted = true;
                    }
                    break;
                }
                case SyncOpType.RemoteCreateFolder:
                    if (waiting.Any(o => o.Type == SyncOpType.RemoteCreateFolder && SamePath(o.RelativePath, op.RelativePath)))
                        return;
                    break;
                case SyncOpType.RemoteDelete:
                {
                    if (waiting.Any(o => o.Type == SyncOpType.RemoteDelete && SamePath(o.RelativePath, op.RelativePath)))
                        return;
                    // Nothing left to upload once it's deleted.
                    foreach (var o in waiting.Where(o =>
                                 (o.Type == SyncOpType.Upload || o.Type == SyncOpType.RemoteCreateFolder) &&
                                 Util.PathUtil.IsSameOrUnder(o.RelativePath, op.RelativePath)))
                        _ops.Delete(o.Id);
                    break;
                }
            }
            _ops.Insert(op);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Operations ready to run, oldest first. An operation waits while an older
    /// one on the same path is still outstanding, and anything under a folder
    /// waits for an older create, rename or delete of that folder, so the cloud
    /// sees changes in the order they were made.
    /// </summary>
    public List<SyncOperation> GetPending(int max = 100, bool ignoreBackoff = false)
    {
        var now = DateTime.UtcNow;
        List<SyncOperation> outstanding;
        lock (_writeLock)
        {
            outstanding = _ops.Find(o =>
                    o.Status == SyncOpStatus.Pending ||
                    o.Status == SyncOpStatus.Retry ||
                    o.Status == SyncOpStatus.InProgress)
                .ToList();
        }

        var busyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var busyTrees = new List<(string Drive, string Path)>();
        var earlier = new List<(string Drive, string Path)>();
        var ready = new List<SyncOperation>();

        foreach (var op in outstanding.OrderBy(o => o.QueuedAt).ThenBy(o => o.Id))
        {
            bool blocked =
                IsBusy(op.DriveConfigId, op.RelativePath, busyPaths, busyTrees) ||
                (op.NewRelativePath != null && IsBusy(op.DriveConfigId, op.NewRelativePath, busyPaths, busyTrees)) ||
                ((op.Type == SyncOpType.RemoteDelete || op.Type == SyncOpType.RemoteRename) &&
                 earlier.Exists(e => e.Drive == op.DriveConfigId &&
                     (Util.PathUtil.IsSameOrUnder(e.Path, op.RelativePath) ||
                      (op.NewRelativePath != null && Util.PathUtil.IsSameOrUnder(e.Path, op.NewRelativePath)))));

            bool due = op.Status != SyncOpStatus.InProgress &&
                       (ignoreBackoff || op.NextAttemptAt == null || op.NextAttemptAt <= now);

            if (!blocked && due && ready.Count < max) ready.Add(op);

            earlier.Add((op.DriveConfigId, op.RelativePath));
            if (op.NewRelativePath != null) earlier.Add((op.DriveConfigId, op.NewRelativePath));
            busyPaths.Add(op.DriveConfigId + "|" + op.RelativePath);
            if (op.NewRelativePath != null) busyPaths.Add(op.DriveConfigId + "|" + op.NewRelativePath);
            if (op.Type != SyncOpType.Upload)
            {
                busyTrees.Add((op.DriveConfigId, op.RelativePath));
                if (op.NewRelativePath != null) busyTrees.Add((op.DriveConfigId, op.NewRelativePath));
            }
        }

        return ready.OrderBy(o => o.Priority).ToList();
    }

    private static bool IsBusy(string drive, string path, HashSet<string> busyPaths,
        List<(string Drive, string Path)> busyTrees)
    {
        if (busyPaths.Contains(drive + "|" + path)) return true;
        foreach (var t in busyTrees)
            if (t.Drive == drive && Util.PathUtil.IsSameOrUnder(path, t.Path)) return true;
        return false;
    }

    /// <summary>Drops uploads and folder creates still waiting for this path or
    /// anything under it. Returns how many were dropped.</summary>
    /// <summary>True while an upload of exactly this path is being sent.</summary>
    public bool HasUploadInProgress(string driveConfigId, string relativePath)
    {
        lock (_writeLock)
        {
            return _ops.Exists(o =>
                o.DriveConfigId == driveConfigId && o.Type == SyncOpType.Upload &&
                o.Status == SyncOpStatus.InProgress && o.RelativePath == relativePath);
        }
    }

    public int CancelWaitingUploads(string driveConfigId, string relativePath)
    {
        lock (_writeLock)
        {
            var doomed = _ops.Find(o =>
                    o.DriveConfigId == driveConfigId &&
                    (o.Type == SyncOpType.Upload || o.Type == SyncOpType.RemoteCreateFolder) &&
                    (o.Status == SyncOpStatus.Pending || o.Status == SyncOpStatus.Retry))
                .Where(o => Util.PathUtil.IsSameOrUnder(o.RelativePath, relativePath))
                .ToList();
            foreach (var o in doomed) _ops.Delete(o.Id);
            return doomed.Count;
        }
    }

    /// <summary>Paths with uploads still to send. Logoff cleanup keeps these files.</summary>
    public List<SyncOperation> GetOutstanding()
    {
        lock (_writeLock)
            return _ops.Find(o =>
                    o.Status == SyncOpStatus.Pending ||
                    o.Status == SyncOpStatus.Retry ||
                    o.Status == SyncOpStatus.InProgress ||
                    o.Status == SyncOpStatus.Failed)
                .ToList();
    }

    public int CountPending()
    {
        return _ops.Count(o =>
            o.Status == SyncOpStatus.Pending || o.Status == SyncOpStatus.Retry);
    }

    /// <summary>Count pending Upload/Delete operations per drive. Used by diagnostic export.</summary>
    public Dictionary<string, int> GetPendingCountsByDrive()
    {
        lock (_writeLock)
        {
            return _ops.Find(o => o.Status == SyncOpStatus.Pending || o.Status == SyncOpStatus.Retry)
                .GroupBy(o => o.DriveConfigId)
                .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>True if this exact (driveConfigId, relativePath) has any pending,
    /// in-progress, or retry op queued. Used by LruEvictionService to skip
    /// files that are mid-sync - evicting one would race with the upload.</summary>
    /// <summary>
    /// True when a local rename or delete of this path, or of a folder above it,
    /// hasn't reached the cloud yet.
    /// </summary>
    public bool HasPendingMoveCovering(string driveConfigId, string relativePath)
    {
        List<SyncOperation> moves;
        lock (_writeLock)
        {
            moves = _ops.Find(o =>
                    (o.Type == SyncOpType.RemoteRename || o.Type == SyncOpType.RemoteDelete) &&
                    o.DriveConfigId == driveConfigId &&
                    (o.Status == SyncOpStatus.Pending || o.Status == SyncOpStatus.InProgress ||
                     o.Status == SyncOpStatus.Retry))
                .ToList();
        }
        return moves.Exists(o =>
            Util.PathUtil.IsSameOrUnder(relativePath, o.RelativePath) ||
            (o.NewRelativePath != null && Util.PathUtil.IsSameOrUnder(relativePath, o.NewRelativePath)));
    }

    public bool HasPendingForPath(string driveConfigId, string relativePath)
    {
        lock (_writeLock)
        {
            var rel = relativePath;
            return _ops.Exists(o =>
                o.DriveConfigId == driveConfigId
                && (o.RelativePath == rel || o.NewRelativePath == rel)
                && (o.Status == SyncOpStatus.Pending
                    || o.Status == SyncOpStatus.InProgress
                    || o.Status == SyncOpStatus.Retry));
        }
    }

    public int CountInProgress() =>
        _ops.Count(o => o.Status == SyncOpStatus.InProgress);

    public int CountFailed() =>
        _ops.Count(o => o.Status == SyncOpStatus.Failed);

    public List<SyncOperation> GetFailed(int max = 100)
    {
        lock (_writeLock)
        {
            return _ops.Query()
                .Where(o => o.Status == SyncOpStatus.Failed)
                .OrderByDescending(o => o.CompletedAt)
                .Limit(max)
                .ToList();
        }
    }

    public bool RetryFailed(string driveConfigId, string relativePath)
    {
        lock (_writeLock)
        {
            var failed = _ops.FindOne(o =>
                o.DriveConfigId == driveConfigId &&
                o.RelativePath == relativePath &&
                o.Status == SyncOpStatus.Failed);
            if (failed == null) return false;
            failed.Status = SyncOpStatus.Pending;
            failed.PermanentFailure = false;
            failed.RetryCount = 0;
            failed.ErrorMessage = null;
            failed.NextAttemptAt = null;
            failed.QueuedAt = DateTime.UtcNow;
            _ops.Update(failed);
            return true;
        }
    }

    public int RetryAllFailed()
    {
        lock (_writeLock)
        {
            var failed = _ops.Query()
                .Where(o => o.Status == SyncOpStatus.Failed)
                .ToList();
            foreach (var op in failed)
            {
                op.Status = SyncOpStatus.Pending;
                op.PermanentFailure = false;
                op.RetryCount = 0;
                op.ErrorMessage = null;
                op.NextAttemptAt = null;
                op.QueuedAt = DateTime.UtcNow;
                _ops.Update(op);
            }
            return failed.Count;
        }
    }

    public void MarkInProgress(SyncOperation op)
    {
        op.Status = SyncOpStatus.InProgress;
        op.StartedAt = DateTime.UtcNow;
        Update(op);
    }

    public void MarkCompleted(SyncOperation op)
    {
        op.Status = SyncOpStatus.Completed;
        op.CompletedAt = DateTime.UtcNow;
        op.ErrorMessage = null;
        Update(op);
    }

    public void MarkRetry(SyncOperation op, TimeSpan backoff, string error)
    {
        op.Status = SyncOpStatus.Retry;
        op.RetryCount++;
        op.NextAttemptAt = DateTime.UtcNow + backoff;
        op.ErrorMessage = error;
        Update(op);
    }

    /// <param name="permanent">False when the op ran out of retries on an error
    /// that may clear up, such as being offline; it is tried again later.</param>
    public void MarkFailed(SyncOperation op, string error, bool permanent = true)
    {
        op.Status = SyncOpStatus.Failed;
        op.PermanentFailure = permanent;
        op.ErrorMessage = error;
        op.CompletedAt = DateTime.UtcNow;
        Update(op);
    }

    public void Update(SyncOperation op)
    {
        lock (_writeLock) _ops.Update(op);
    }

    public void PurgeCompleted(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        lock (_writeLock)
        {
            _ops.DeleteMany(o => o.Status == SyncOpStatus.Completed && o.CompletedAt < cutoff);
        }
    }

    public void ResetInProgressOnStartup()
    {
        lock (_writeLock)
        {
            // Any InProgress ops at startup were interrupted - reset to Pending
            var stuck = _ops.Query().Where(o => o.Status == SyncOpStatus.InProgress).ToList();
            foreach (var op in stuck)
            {
                op.Status = SyncOpStatus.Pending;
                op.StartedAt = null;
                _ops.Update(op);
            }
            if (stuck.Count > 0)
                _logger.Information("Reset {Count} interrupted ops back to Pending on startup", stuck.Count);

            // Failed delete/rename ops should be retried - leaving them failed
            // means the delta poller will recreate the placeholder next poll.
            // So should anything that only ran out of retries.
            var failedSyncOps = _ops.Query()
                .Where(o => o.Status == SyncOpStatus.Failed)
                .ToList()
                .FindAll(o => o.Type == SyncOpType.RemoteDelete || o.Type == SyncOpType.RemoteRename ||
                              !o.PermanentFailure);
            foreach (var op in failedSyncOps)
            {
                op.Status = SyncOpStatus.Pending;
                op.PermanentFailure = false;
                op.RetryCount = 0;
                op.StartedAt = null;
                op.ErrorMessage = null;
                _ops.Update(op);
            }
            if (failedSyncOps.Count > 0)
                _logger.Information("Reset {Count} failed ops back to Pending on startup", failedSyncOps.Count);
        }
    }

    /// <summary>
    /// Puts ops that ran out of retries some time ago back in the queue, so an
    /// upload that failed while offline is sent once the connection is back.
    /// </summary>
    public int RearmTransientFailures(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        lock (_writeLock)
        {
            var due = _ops.Query()
                .Where(o => o.Status == SyncOpStatus.Failed)
                .ToList()
                .FindAll(o => !o.PermanentFailure && o.CompletedAt < cutoff);
            foreach (var op in due)
            {
                op.Status = SyncOpStatus.Pending;
                op.RetryCount = 0;
                op.ErrorMessage = null;
                op.NextAttemptAt = null;
                op.QueuedAt = DateTime.UtcNow;
                _ops.Update(op);
            }
            if (due.Count > 0)
                _logger.Information("Retrying {Count} op(s) that failed earlier", due.Count);
            return due.Count;
        }
    }

    /// <summary>
    /// Removes operations the caller's predicate marks as stale - e.g. queued ops
    /// whose local file no longer exists, or terminally-Failed ops that will never
    /// succeed. Considers Pending, Retry and Failed ops; InProgress and Completed
    /// are left alone.
    /// </summary>
    public int RemoveStaleOperations(System.Func<SyncOperation, bool> isStale)
    {
        lock (_writeLock)
        {
            var candidates = _ops.Query()
                .Where(o => o.Status == SyncOpStatus.Pending
                         || o.Status == SyncOpStatus.Retry
                         || o.Status == SyncOpStatus.Failed)
                .ToList();
            int removed = 0;
            foreach (var op in candidates)
            {
                if (isStale(op))
                {
                    _ops.Delete(op.Id);
                    removed++;
                }
            }
            return removed;
        }
    }

    public string? GetDeltaToken(string driveConfigId)
    {
        return _deltaTokens.FindOne(t => t.DriveConfigId == driveConfigId)?.Token;
    }

    public void SetDeltaToken(string driveConfigId, string token)
    {
        lock (_writeLock)
        {
            var existing = _deltaTokens.FindOne(t => t.DriveConfigId == driveConfigId);
            if (existing != null)
            {
                existing.Token = token;
                existing.UpdatedAt = DateTime.UtcNow;
                _deltaTokens.Update(existing);
            }
            else
            {
                _deltaTokens.Insert(new DeltaToken
                {
                    DriveConfigId = driveConfigId,
                    Token = token,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
        }
    }

    public void ClearDeltaToken(string driveConfigId)
    {
        lock (_writeLock)
        {
            _deltaTokens.DeleteMany(t => t.DriveConfigId == driveConfigId);
        }
    }

    /// <summary>Drops every drive's delta token. Used by SessionCacheCleaner so the
    /// next mount starts with a ?token=latest bootstrap (consistent with the freshly-
    /// wiped metadata cache). Pending sync_ops are NOT touched.</summary>
    public int ClearAllDeltaTokens()
    {
        lock (_writeLock)
        {
            return _deltaTokens.DeleteAll();
        }
    }

    public string? GetDeltaCheckpoint(string driveConfigId)
    {
        return _deltaTokens.FindOne(t => t.DriveConfigId == driveConfigId)?.Checkpoint;
    }

    public void SetDeltaCheckpoint(string driveConfigId, string nextLink)
    {
        lock (_writeLock)
        {
            var existing = _deltaTokens.FindOne(t => t.DriveConfigId == driveConfigId);
            if (existing != null)
            {
                existing.Checkpoint = nextLink;
                existing.UpdatedAt = DateTime.UtcNow;
                _deltaTokens.Update(existing);
            }
            else
            {
                _deltaTokens.Insert(new DeltaToken
                {
                    DriveConfigId = driveConfigId,
                    Checkpoint = nextLink,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
        }
    }

    public void ClearDeltaCheckpoint(string driveConfigId)
    {
        lock (_writeLock)
        {
            var existing = _deltaTokens.FindOne(t => t.DriveConfigId == driveConfigId);
            if (existing != null)
            {
                existing.Checkpoint = null;
                _deltaTokens.Update(existing);
            }
        }
    }

    public IEnumerable<SyncOperation> AllOperations() => _ops.FindAll();

    /// <summary>Compacts the LiteDB file in place: Checkpoint folds the WAL log
    /// back into the main file, Rebuild defragments freed pages from completed /
    /// deleted operations. Pending sync_ops + delta tokens are preserved - this is
    /// safe to call at any time. Best-effort; swallows exceptions.</summary>
    public void Compact()
    {
        lock (_writeLock)
        {
            try
            {
                _db.Checkpoint();
                _db.Rebuild();
            }
            catch
            {
                // Compaction is opportunistic - a failure here doesn't compromise
                // correctness, just leaves the file un-shrunk until next attempt.
            }
        }
    }

    public void Dispose()
    {
        try { _db?.Dispose(); } catch { }
    }
}

internal sealed class DeltaToken
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public string DriveConfigId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string? Checkpoint { get; set; }
    public DateTime UpdatedAt { get; set; }
}
