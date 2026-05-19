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

        _deltaTokens = _db.GetCollection<DeltaToken>(DeltaCollectionName);
        _deltaTokens.EnsureIndex(t => t.DriveConfigId, unique: true);
    }

    public void Enqueue(SyncOperation op)
    {
        lock (_writeLock)
        {
            // Coalesce: if a Pending or Retry upload for the same path exists, replace it
            if (op.Type == SyncOpType.Upload)
            {
                var existing = _ops.FindOne(o =>
                    o.DriveConfigId == op.DriveConfigId &&
                    o.RelativePath == op.RelativePath &&
                    o.Type == SyncOpType.Upload &&
                    (o.Status == SyncOpStatus.Pending || o.Status == SyncOpStatus.Retry));
                if (existing != null)
                {
                    existing.QueuedAt = DateTime.UtcNow;
                    existing.FileSizeBytes = op.FileSizeBytes;
                    existing.ContentHash = op.ContentHash;
                    existing.Status = SyncOpStatus.Pending;
                    existing.ErrorMessage = null;
                    _ops.Update(existing);
                    return;
                }
            }
            _ops.Insert(op);
        }
    }

    public List<SyncOperation> GetPending(int max = 100)
    {
        var now = DateTime.UtcNow;
        lock (_writeLock)
        {
            return _ops.Query()
                .Where(o => (o.Status == SyncOpStatus.Pending || o.Status == SyncOpStatus.Retry)
                            && (o.NextAttemptAt == null || o.NextAttemptAt <= now))
                .OrderBy(o => o.Priority)
                .Limit(max)
                .ToList();
        }
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
    /// files that are mid-sync — evicting one would race with the upload.</summary>
    public bool HasPendingForPath(string driveConfigId, string relativePath)
    {
        lock (_writeLock)
        {
            return _ops.Exists(o =>
                o.DriveConfigId == driveConfigId
                && o.RelativePath == relativePath
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

    public void MarkFailed(SyncOperation op, string error)
    {
        op.Status = SyncOpStatus.Failed;
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

            // Failed delete/rename ops should be retried — leaving them failed
            // means the delta poller will recreate the placeholder next poll.
            var failedSyncOps = _ops.Query()
                .Where(o => o.Status == SyncOpStatus.Failed &&
                       (o.Type == SyncOpType.RemoteDelete || o.Type == SyncOpType.RemoteRename))
                .ToList();
            foreach (var op in failedSyncOps)
            {
                op.Status = SyncOpStatus.Pending;
                op.RetryCount = 0;
                op.StartedAt = null;
                op.ErrorMessage = null;
                _ops.Update(op);
            }
            if (failedSyncOps.Count > 0)
                _logger.Information("Reset {Count} failed delete/rename ops back to Pending on startup", failedSyncOps.Count);
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
    /// deleted operations. Pending sync_ops + delta tokens are preserved — this is
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
                // Compaction is opportunistic — a failure here doesn't compromise
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
