using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Persistent store of remote (Graph) file metadata so we can render placeholders
/// in Explorer and look up where each local file's content lives in the cloud.
/// </summary>
internal sealed class MetadataStore : IDisposable
{
    private const string Collection = "remote_items";
    private const string DeletedCollection = "deleted_items";
    private const string EnumStateCollection = "folder_enum_state";
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RemoteItem> _items;
    private readonly ILiteCollection<DeletedItem> _deleted;
    private readonly ILiteCollection<FolderEnumerationState> _enumState;
    private readonly ILogger _logger;
    private readonly object _writeLock = new();

    public MetadataStore(string databasePath, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _logger = logger;
        _db = new LiteDatabase(new ConnectionString
        {
            Filename = databasePath,
            // Shared so OneSync.exe --launch-office (a short-lived second
            // instance spawned by file associations) can read the same DB
            // while the main service is running. Slight perf cost vs Direct.
            Connection = ConnectionType.Shared,
        });
        _items = _db.GetCollection<RemoteItem>(Collection);
        _items.EnsureIndex(i => i.DriveConfigId);
        _items.EnsureIndex(i => i.RelativePath);
        _items.EnsureIndex(i => i.RemoteItemId);
        _items.EnsureIndex(i => i.ParentRemoteItemId);
        _items.EnsureIndex(i => i.Hydrated);

        _deleted = _db.GetCollection<DeletedItem>(DeletedCollection);
        _deleted.EnsureIndex(i => i.DriveConfigId);
        _deleted.EnsureIndex(i => i.DeletedAtUtc);

        _enumState = _db.GetCollection<FolderEnumerationState>(EnumStateCollection);
        _enumState.EnsureIndex(i => i.DriveConfigId);
        _enumState.EnsureIndex(i => i.LastAttemptUtc);

        var cutoff = DateTime.UtcNow.AddDays(-93);
        _deleted.DeleteMany(i => i.DeletedAtUtc < cutoff);

        // Drop very-old enumeration state so the table doesn't grow unbounded
        var enumCutoff = DateTime.UtcNow.AddDays(-30);
        _enumState.DeleteMany(i => i.LastAttemptUtc < enumCutoff);

        // One-time cleanup of orphan records from versions 1.0.15 and 1.0.16,
        // which wrote FolderEnumerationState without [BsonId] on the Key
        // property. Those records have ObjectId _id values (not the composite
        // string key), so FindById never finds them — they're inert junk that
        // wastes disk space until the 30-day TTL above sweeps them. Delete any
        // record whose serialized DriveConfigId is empty (the auto-id'd records
        // round-trip with the Key field stored but never populated correctly).
        try
        {
            var orphans = _enumState.DeleteMany(i => i.DriveConfigId == "" || i.Key == "");
            if (orphans > 0)
                _logger.Information("Dropped {Count} orphan folder_enum_state records (pre-1.0.17)", orphans);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Orphan enum-state cleanup failed (non-fatal)");
        }
    }

    public RemoteItem? Get(string driveConfigId, string relativePath)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        return _items.FindById(key);
    }

    public RemoteItem? GetById(string remoteItemId) =>
        _items.FindOne(i => i.RemoteItemId == remoteItemId);

    public List<RemoteItem> GetForDrive(string driveConfigId)
    {
        lock (_writeLock) return _items.Find(i => i.DriveConfigId == driveConfigId).ToList();
    }

    /// <summary>Direct children of a folder. For non-root paths, uses ParentRemoteItemId
    /// (indexed lookup, fast). For the root path "/", the parent isn't in metadata as
    /// a row — falls back to a client-side filter on RelativePath shape (a direct child
    /// of root has exactly one "/", at position 0).
    ///
    /// Used by HydrationService.EnumerateFolderIfEmpty to self-heal partial folders:
    /// when local disk has fewer placeholders than metadata says, materialise the missing
    /// ones without needing a Graph round-trip. Returns empty if the parent isn't in
    /// metadata (caller should fall through to lazy-fallback Graph call).</summary>
    public List<RemoteItem> GetDirectChildrenByPath(string driveConfigId, string parentRelativePath)
    {
        // Root case: parent isn't tracked as a metadata row. Scan all items for the drive
        // and filter to those whose path has only one "/" (direct root children like "/Folder").
        // O(N) per call but capped to one call per drive per session (in-memory dedup).
        if (string.IsNullOrEmpty(parentRelativePath) || parentRelativePath == "/")
        {
            lock (_writeLock)
                return _items.Find(i => i.DriveConfigId == driveConfigId)
                    .Where(i => !string.IsNullOrEmpty(i.RelativePath)
                                && i.RelativePath.StartsWith("/")
                                && i.RelativePath.IndexOf('/', 1) < 0)
                    .ToList();
        }

        var parent = Get(driveConfigId, parentRelativePath);
        if (parent is null || string.IsNullOrEmpty(parent.RemoteItemId))
            return new List<RemoteItem>();
        var pid = parent.RemoteItemId;
        lock (_writeLock)
            return _items.Find(i => i.DriveConfigId == driveConfigId && i.ParentRemoteItemId == pid).ToList();
    }

    public List<RemoteItem> GetUnhydratedForDrive(string driveConfigId)
    {
        lock (_writeLock) return _items.Find(i => i.DriveConfigId == driveConfigId && !i.Hydrated && !i.IsFolder).ToList();
    }

    public void Upsert(RemoteItem item)
    {
        item.Key = RemoteItem.MakeKey(item.DriveConfigId, item.RelativePath);
        item.LastSyncedAt = DateTime.UtcNow;
        lock (_writeLock) _items.Upsert(item);
    }

    public void MarkHydrated(string driveConfigId, string relativePath, string? actualETag = null)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var existing = _items.FindById(key);
            if (existing == null) return;
            existing.Hydrated = true;
            existing.LastSyncedAt = DateTime.UtcNow;
            existing.LastAccessedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(actualETag))
                existing.ETag = actualETag;
            _items.Update(existing);
        }
    }

    /// <summary>Records that a hydrated file has just been truncated back to a
    /// placeholder by the LRU eviction service. The metadata row stays; only
    /// the Hydrated flag flips. The cloud copy is UNTOUCHED — this method
    /// performs zero remote operations.</summary>
    public void MarkDehydrated(string driveConfigId, string relativePath)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var existing = _items.FindById(key);
            if (existing == null) return;
            existing.Hydrated = false;
            existing.LastSyncedAt = DateTime.UtcNow;
            _items.Update(existing);
        }
    }

    /// <summary>Update LastAccessedAt for an item (called by Dokan on file open
    /// so the LRU service knows recency). Writes through immediately so a
    /// crash doesn't lose recency information for actively-used files.</summary>
    public void TouchLastAccessed(string driveConfigId, string relativePath)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var existing = _items.FindById(key);
            if (existing == null) return;
            existing.LastAccessedAt = DateTime.UtcNow;
            _items.Update(existing);
        }
    }

    /// <summary>Enumerate currently-hydrated, non-folder items for a drive,
    /// ordered by LastAccessedAt ascending (oldest first). Used by LruEvictionService.</summary>
    public List<RemoteItem> GetHydratedByLastAccessed(string driveConfigId)
    {
        lock (_writeLock)
        {
            var all = _items.Find(i => i.DriveConfigId == driveConfigId).ToList();
            var hydrated = all.Where(i => i.Hydrated && !i.IsFolder).ToList();
            _logger.Information(
                "GetHydratedByLastAccessed({Drive}): {Total} total, {Hydrated} hydrated non-folder",
                driveConfigId, all.Count, hydrated.Count);
            return hydrated.OrderBy(i => i.LastAccessedAt).ToList();
        }
    }

    /// <summary>Records a new ETag after a successful upload (the server returns
    /// the post-upload eTag in the response). Creates a metadata record if none
    /// exists yet (first upload of a brand-new local file).</summary>
    public void UpdateETag(string driveConfigId, string relativePath, string newETag,
        string? remoteItemId = null, long fileSizeBytes = 0)
    {
        if (string.IsNullOrEmpty(newETag)) return;
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var existing = _items.FindById(key);
            if (existing != null)
            {
                existing.ETag = newETag;
                if (!string.IsNullOrEmpty(remoteItemId)) existing.RemoteItemId = remoteItemId;
                if (fileSizeBytes > 0) existing.Size = fileSizeBytes;
                existing.Hydrated = true;
                existing.LastSyncedAt = DateTime.UtcNow;
                // Bump LastAccessedAt too: a file we just successfully uploaded
                // is the most recently-touched content on disk. Without this
                // bump, LruEvictionService would pick freshly-uploaded files
                // as victims immediately on a near-full disk.
                existing.LastAccessedAt = DateTime.UtcNow;
                _items.Update(existing);
            }
            else
            {
                var name = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar));
                _items.Upsert(new RemoteItem
                {
                    Key = key,
                    DriveConfigId = driveConfigId,
                    RelativePath = relativePath,
                    Name = name,
                    RemoteItemId = remoteItemId ?? string.Empty,
                    ETag = newETag,
                    Size = fileSizeBytes,
                    Hydrated = true,
                    PlaceholderCreated = true,
                    LastSyncedAt = DateTime.UtcNow,
                });
            }
        }
    }

    public void MarkPlaceholderCreated(string driveConfigId, string relativePath)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var existing = _items.FindById(key);
            if (existing == null) return;
            existing.PlaceholderCreated = true;
            _items.Update(existing);
        }
    }

    public bool Delete(string driveConfigId, string relativePath)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock) return _items.Delete(key);
    }

    public int CountFor(string driveConfigId) =>
        _items.Count(i => i.DriveConfigId == driveConfigId);

    public void ClearAll()
    {
        lock (_writeLock) _items.DeleteAll();
    }

    public int CountHydratedFor(string driveConfigId) =>
        _items.Count(i => i.DriveConfigId == driveConfigId && i.Hydrated);

    public void AddDeleted(DeletedItem item)
    {
        item.Key = DeletedItem.MakeKey(item.DriveConfigId, item.RemoteItemId);
        lock (_writeLock) _deleted.Upsert(item);
    }

    public List<DeletedItem> GetRecentlyDeleted(int max = 50)
    {
        lock (_writeLock)
            return _deleted.Query()
                .OrderByDescending(i => i.DeletedAtUtc)
                .Limit(max)
                .ToList();
    }

    public bool RemoveDeleted(string key)
    {
        lock (_writeLock) return _deleted.Delete(key);
    }

    public FolderEnumerationState? GetEnumerationState(string driveConfigId, string relativePath)
    {
        var key = FolderEnumerationState.MakeKey(driveConfigId, relativePath);
        return _enumState.FindById(key);
    }

    public void RecordEnumerationSuccess(string driveConfigId, string relativePath)
    {
        var key = FolderEnumerationState.MakeKey(driveConfigId, relativePath);
        var record = new FolderEnumerationState
        {
            Key = key,
            DriveConfigId = driveConfigId,
            RelativePath = relativePath,
            LastStatusCode = 200,
            LastAttemptUtc = DateTime.UtcNow,
            NegativeCacheExpiresUtc = DateTime.MinValue,
        };
        lock (_writeLock) _enumState.Upsert(record);
    }

    public void RecordEnumerationNotFound(string driveConfigId, string relativePath, TimeSpan ttl)
    {
        var key = FolderEnumerationState.MakeKey(driveConfigId, relativePath);
        var now = DateTime.UtcNow;
        var record = new FolderEnumerationState
        {
            Key = key,
            DriveConfigId = driveConfigId,
            RelativePath = relativePath,
            LastStatusCode = 404,
            LastAttemptUtc = now,
            NegativeCacheExpiresUtc = now + ttl,
        };
        lock (_writeLock) _enumState.Upsert(record);
    }

    /// <summary>Drop all enumeration state for a drive - used when the delta
    /// token rotates (full re-sync, server-side reset) so we don't keep stale
    /// negative-cache entries pointing at folders that may now exist.</summary>
    public void ClearEnumerationStateForDrive(string driveConfigId)
    {
        lock (_writeLock)
            _enumState.DeleteMany(i => i.DriveConfigId == driveConfigId);
    }

    /// <summary>Delete all RemoteItem and FolderEnumerationState records for a single drive.
    /// Used by Force-resync — the next delta poll re-fetches everything from /delta.
    /// Local hydrated files and placeholders on disk are NOT touched; they get rebuilt
    /// as the delta poll re-discovers each item.</summary>
    public void ClearForDrive(string driveConfigId)
    {
        lock (_writeLock)
        {
            var itemsDeleted = _items.DeleteMany(i => i.DriveConfigId == driveConfigId);
            var enumDeleted = _enumState.DeleteMany(i => i.DriveConfigId == driveConfigId);
            _logger.Information(
                "Cleared metadata for drive {DriveId}: {Items} items, {Enum} enumeration records",
                driveConfigId, itemsDeleted, enumDeleted);
        }
    }

    public void Dispose() => _db?.Dispose();
}
