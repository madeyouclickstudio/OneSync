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
        // string key), so FindById never finds them - they're inert junk that
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
    /// a row - falls back to a client-side filter on RelativePath shape (a direct child
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

    public void MarkHydrated(string driveConfigId, string relativePath, string? actualETag = null,
        FileInfo? localFile = null)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var existing = _items.FindById(key);
            if (existing == null) return;
            existing.Hydrated = true;
            existing.RemoteChangedSinceBaseline = false;
            existing.LastSyncedAt = DateTime.UtcNow;
            existing.LastAccessedAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(actualETag))
                existing.ETag = actualETag;
            if (localFile != null)
                LocalState.SetBaseline(existing, localFile);
            _items.Update(existing);
        }
    }

    /// <summary>Records that a hydrated file has just been truncated back to a
    /// placeholder by the LRU eviction service. The metadata row stays; only
    /// the Hydrated flag flips. The cloud copy is UNTOUCHED - this method
    /// performs zero remote operations.</summary>
    public void MarkDehydrated(string driveConfigId, string relativePath)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var existing = _items.FindById(key);
            if (existing == null) return;
            existing.Hydrated = false;
            LocalState.ClearBaseline(existing);
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

    /// <summary>
    /// Records a successful upload: the server's new eTag, id and hash, plus the
    /// local file's length and write time as they were when we read it to upload.
    /// If the file changed while it was uploading, that baseline no longer matches
    /// and the next pass uploads it again.
    /// </summary>
    public void RecordUpload(string driveConfigId, string relativePath, UploadResult result,
        long localLength, long localWriteTicks)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var item = _items.FindById(key) ?? new RemoteItem
            {
                Key = key,
                DriveConfigId = driveConfigId,
                RelativePath = relativePath,
                Name = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar)),
                PlaceholderCreated = true,
            };
            if (!string.IsNullOrEmpty(result.ETag)) item.ETag = result.ETag;
            if (!string.IsNullOrEmpty(result.CTag)) item.CTag = result.CTag;
            if (!string.IsNullOrEmpty(result.RemoteItemId)) item.RemoteItemId = result.RemoteItemId;
            if (!string.IsNullOrEmpty(result.ParentRemoteItemId)) item.ParentRemoteItemId = result.ParentRemoteItemId;
            if (!string.IsNullOrEmpty(result.WebUrl)) item.WebUrl = result.WebUrl;
            if (!string.IsNullOrEmpty(result.QuickXorHash)) item.ContentHash = result.QuickXorHash;
            if (result.LastModified != default) item.LastModifiedDateTime = result.LastModified;
            if (result.Created != default) item.CreatedDateTime = result.Created;
            item.Size = result.Size >= 0 ? result.Size : localLength;
            item.IsFolder = false;
            item.Hydrated = true;
            item.RemoteChangedSinceBaseline = false;
            item.LocalLength = localLength;
            item.LocalWriteTicks = localWriteTicks;
            item.LastSyncedAt = DateTime.UtcNow;
            item.LastAccessedAt = DateTime.UtcNow;
            _items.Upsert(item);
        }
    }

    /// <summary>Refreshes the cloud identifiers of an item after a rename or move,
    /// leaving its content baseline alone.</summary>
    public void UpdateRemoteFields(string driveConfigId, string relativePath, UploadResult result)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var item = _items.FindById(key);
            if (item is null) return;
            if (!string.IsNullOrEmpty(result.ETag)) item.ETag = result.ETag;
            if (!string.IsNullOrEmpty(result.CTag)) item.CTag = result.CTag;
            if (!string.IsNullOrEmpty(result.RemoteItemId)) item.RemoteItemId = result.RemoteItemId;
            if (!string.IsNullOrEmpty(result.ParentRemoteItemId)) item.ParentRemoteItemId = result.ParentRemoteItemId;
            if (!string.IsNullOrEmpty(result.WebUrl)) item.WebUrl = result.WebUrl;
            item.LastSyncedAt = DateTime.UtcNow;
            _items.Update(item);
        }
    }

    /// <summary>Records a folder we created in the cloud.</summary>
    public void RecordFolder(string driveConfigId, string relativePath, UploadResult result)
    {
        var key = RemoteItem.MakeKey(driveConfigId, relativePath);
        lock (_writeLock)
        {
            var item = _items.FindById(key) ?? new RemoteItem
            {
                Key = key,
                DriveConfigId = driveConfigId,
                RelativePath = relativePath,
                Name = Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar)),
            };
            item.IsFolder = true;
            item.PlaceholderCreated = true;
            if (!string.IsNullOrEmpty(result.ETag)) item.ETag = result.ETag;
            if (!string.IsNullOrEmpty(result.RemoteItemId)) item.RemoteItemId = result.RemoteItemId;
            if (!string.IsNullOrEmpty(result.ParentRemoteItemId)) item.ParentRemoteItemId = result.ParentRemoteItemId;
            if (!string.IsNullOrEmpty(result.WebUrl)) item.WebUrl = result.WebUrl;
            item.LastSyncedAt = DateTime.UtcNow;
            _items.Upsert(item);
        }
    }

    /// <summary>Moves an item (and, for a folder, everything under it) to a new path
    /// after a rename or move.</summary>
    public void MovePath(string driveConfigId, string oldPath, string newPath)
    {
        lock (_writeLock)
        {
            var prefix = oldPath.TrimEnd('/') + "/";
            var affected = _items.Find(i => i.DriveConfigId == driveConfigId).Where(i =>
                    i.RelativePath.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
                    i.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var item in affected)
            {
                _items.Delete(item.Key);
                item.RelativePath = newPath + item.RelativePath[oldPath.Length..];
                if (item.RelativePath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                    item.Name = Path.GetFileName(newPath.Replace('/', Path.DirectorySeparatorChar));
                item.Key = RemoteItem.MakeKey(driveConfigId, item.RelativePath);
                _items.Upsert(item);
            }
        }
    }

    /// <summary>Deletes an item and, for a folder, everything recorded under it.</summary>
    public int DeleteTree(string driveConfigId, string relativePath)
    {
        lock (_writeLock)
        {
            var prefix = relativePath.TrimEnd('/') + "/";
            var doomed = _items.Find(i => i.DriveConfigId == driveConfigId).Where(i =>
                    i.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase) ||
                    i.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Key)
                .ToList();
            foreach (var key in doomed) _items.Delete(key);
            return doomed.Count;
        }
    }

    /// <summary>
    /// Drops every record except files with a local copy and the folders above
    /// them (or every folder, with <paramref name="keepFolders"/>), so the next
    /// browse of each folder fetches a fresh listing while the records that
    /// protect local edits survive.
    /// </summary>
    public int PruneToLocalCopies(string? driveConfigId = null, bool keepFolders = false)
    {
        lock (_writeLock)
        {
            var all = driveConfigId is null
                ? _items.FindAll().ToList()
                : _items.Find(i => i.DriveConfigId == driveConfigId).ToList();

            var keep = new HashSet<string>(StringComparer.Ordinal);
            if (keepFolders)
                foreach (var folder in all.Where(i => i.IsFolder)) keep.Add(folder.Key);
            foreach (var item in all.Where(i => !i.IsFolder && (i.Hydrated || LocalState.HasBaseline(i))))
            {
                keep.Add(item.Key);
                var parent = Util.PathUtil.ParentOf(item.RelativePath);
                while (parent != "/")
                {
                    if (!keep.Add(RemoteItem.MakeKey(item.DriveConfigId, parent))) break;
                    parent = Util.PathUtil.ParentOf(parent);
                }
            }

            int removed = 0;
            foreach (var item in all)
                if (!keep.Contains(item.Key) && _items.Delete(item.Key)) removed++;
            return removed;
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

    /// <summary>Forgets what we know about a drive's cloud listing so every folder
    /// is fetched again when next opened. Records that protect local copies stay.</summary>
    public void ClearForDrive(string driveConfigId)
    {
        lock (_writeLock)
        {
            var itemsDeleted = PruneToLocalCopies(driveConfigId, keepFolders: true);
            var enumDeleted = _enumState.DeleteMany(i => i.DriveConfigId == driveConfigId);
            _logger.Information(
                "Cleared metadata for drive {DriveId}: {Items} items, {Enum} enumeration records",
                driveConfigId, itemsDeleted, enumDeleted);
        }
    }

    public void ClearAllEnumerationState()
    {
        lock (_writeLock) _enumState.DeleteAll();
    }

    /// <summary>Reclaims the space left by deleted records. Best effort.</summary>
    public void Compact()
    {
        lock (_writeLock)
        {
            try
            {
                _db.Checkpoint();
                _db.Rebuild();
            }
            catch { }
        }
    }

    public void Dispose() => _db?.Dispose();
}
