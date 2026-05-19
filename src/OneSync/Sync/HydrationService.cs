using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.FileSystem;
using OneSync.Shell;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Downloads remote file content from Graph API on demand and writes it to the
/// local placeholder file, converting it to a fully-hydrated NTFS file.
/// Subsequent reads are served from disk.
/// </summary>
internal sealed class HydrationService : IHydrationTrigger, IDisposable
{
    private readonly LocalChangeSuppressor _suppressor;
    private readonly SemaphoreSlim _hydrationGate;
    private readonly PlaceholderManager _placeholders;

    // Tracks folders already enumerated this session to avoid redundant API calls.
    // Cross-session deduplication lives in MetadataStore (FolderEnumerationState);
    // this in-memory set is just a fast-path to avoid hitting LiteDB on every
    // FindFiles call during a heavy directory walk.
    private readonly ConcurrentDictionary<string, byte> _enumeratedFolders = new(StringComparer.OrdinalIgnoreCase);

    // How long to honour a "remote folder doesn't exist" 404 before re-checking.
    // 24h is long enough that PS Modules / node_modules / .git walks during a
    // session don't re-storm Graph, but short enough that newly-created remote
    // folders show up within a day even without a delta poll catching them.
    private static readonly TimeSpan NotFoundCacheTtl = TimeSpan.FromHours(24);

    /// <summary>Fires when a hydration is refused (403) so the tray can balloon the user.
    /// (DriveConfig, relativePath, serverMessage).</summary>
    public event Action<DriveConfig, string, string?>? HydrationDenied;

    // Counts on-demand /children calls currently in flight. The tray polls this so
    // users see "Loading 2 folders…" while Explorer briefly shows empty during the
    // ~50-200ms Graph round trip after a session-cache wipe.
    private int _activeEnumerations;
    public int ActiveEnumerationCount => Volatile.Read(ref _activeEnumerations);

    /// <summary>Fires when the in-flight enumeration count changes (debounced to
    /// transition events: 0→N and N→0). Tray uses this to update the tooltip.</summary>
    public event Action<int>? ActiveEnumerationsChanged;

    /// <summary>
    /// Master switch for on-demand folder enumeration triggered by Dokan FindFiles.
    /// When false, never-before-seen folders won't appear in Explorer until the
    /// delta poller (or cooperative reader merge) reaches them. Defaults to true
    /// to preserve historical behaviour; cooperative polling drives this from
    /// <c>CooperativePollingConfig.LazyFallbackEnabled</c>.
    /// </summary>
    public bool LazyFallbackEnabled { get; set; } = true;

    public long GetRemoteSize(DriveConfig drive, string relativePath)
    {
        var meta = _metadata.Get(drive.ConfigId, relativePath);
        return meta == null || meta.IsFolder ? 0L : meta.Size;
    }

    /// <summary>True if Graph is currently throttled. Callers on the synchronous
    /// Dokan thread should check this BEFORE invoking HydrateIfNeeded so a slow
    /// cooldown doesn't lock Explorer's file-open thread for up to 10 minutes.</summary>
    public bool IsGraphInCooldown => _graph.IsInCooldown;

    /// <summary>Updates the metadata LastAccessedAt for a file so the LRU
    /// eviction service doesn't pick it as a victim. Called from Dokan
    /// CreateFile on every file open. Best-effort; never throws upward.</summary>
    public void NotifyAccessed(DriveConfig drive, string relativePath)
    {
        try { _metadata.TouchLastAccessed(drive.ConfigId, relativePath); }
        catch (Exception ex) { _logger.Debug(ex, "TouchLastAccessed failed for {Path}", relativePath); }
    }

    private readonly GraphHttpClient _graph;
    private readonly MetadataStore _metadata;
    private readonly Dictionary<string, DriveConfig> _drivesById;
    private readonly ILogger _logger;

    // Per-path locks so concurrent reads don't trigger duplicate downloads
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public HydrationService(
        GraphHttpClient graph,
        MetadataStore metadata,
        PlaceholderManager placeholders,
        IEnumerable<DriveConfig> drives,
        LocalChangeSuppressor suppressor,
        SyncSettings settings,
        ILogger logger)
    {
        _graph = graph;
        _metadata = metadata;
        _placeholders = placeholders;
        _suppressor = suppressor;
        _logger = logger.ForContext("Component", "HydrationService");
        _drivesById = new Dictionary<string, DriveConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drives) _drivesById[d.ConfigId] = d;
        var hydMax = Math.Max(1, settings.MaxConcurrentHydrations);
        _hydrationGate = new SemaphoreSlim(hydMax, hydMax);
    }

    /// <summary>
    /// Ensures the file at <paramref name="localPath"/> is fully hydrated.
    /// Returns true if hydrated (either was already, or just was). False if no
    /// remote metadata exists or hydration failed.
    /// </summary>
    public async Task<bool> HydrateIfNeededAsync(
        DriveConfig drive, string relativePath, string localPath, CancellationToken ct = default)
    {
        var meta = _metadata.Get(drive.ConfigId, relativePath);
        if (meta == null || meta.IsFolder) return false;

        if (meta.Hydrated && File.Exists(localPath) && new FileInfo(localPath).Length == meta.Size)
            return true; // already done

        var sem = _locks.GetOrAdd(localPath, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            // Re-check after acquiring lock - another thread may have done it
            meta = _metadata.Get(drive.ConfigId, relativePath);
            if (meta == null) return false;
            if (meta.Hydrated && File.Exists(localPath) && new FileInfo(localPath).Length == meta.Size)
                return true;

            return await DownloadAsync(drive, meta, localPath, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Synchronous variant for use by Dokan ReadFile/CreateFile callbacks (Dokan
    /// methods are sync). Blocks the calling thread until hydration completes.
    /// </summary>
    public bool HydrateIfNeeded(DriveConfig drive, string relativePath, string localPath)
    {
        try
        {
            return HydrateIfNeededAsync(drive, relativePath, localPath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Hydration failed for {Drive}:{Path}", drive.Letter, relativePath);
            return false;
        }
    }

    private async Task<bool> DownloadAsync(DriveConfig drive, RemoteItem item, string localPath, CancellationToken ct)
    {
        await _hydrationGate.WaitAsync(ct);
        try
        {
            return await DownloadCoreAsync(drive, item, localPath, ct);
        }
        finally
        {
            _hydrationGate.Release();
        }
    }

    private async Task<bool> DownloadCoreAsync(DriveConfig drive, RemoteItem item, string localPath, CancellationToken ct)
    {
        _logger.Information("Hydrating: {Drive}:{Path} ({Size} bytes)",
            drive.Letter, item.RelativePath, item.Size);

        var url = BuildItemContentUrl(drive, item);
        using var resp = await _graph.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            ct, HttpCompletionOption.ResponseHeadersRead);

        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var msg = ExtractServerMessage(body);
            _logger.Warning("Hydration denied (403) for {Drive}:{Path}: {Msg}",
                drive.Letter, item.RelativePath, msg ?? "(no detail)");

            // Leave the placeholder as 0 bytes but mark it Error so the user gets
            // a red overlay and knows opening it won't work.
            try { SyncStateMarker.Mark(localPath, SyncOverlayState.Error); } catch { }

            try { HydrationDenied?.Invoke(drive, item.RelativePath, msg); } catch { }
            return false;
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.Warning("Hydration HTTP {Status} for {Drive}:{Path}: {Body}",
                (int)resp.StatusCode, drive.Letter, item.RelativePath, Truncate(body, 300));
            return false;
        }

        // Capture the eTag we hydrated from - this is the version of the cloud
        // content sitting on disk. Future uploads will send it as If-Match.
        string? hydratedETag = resp.Headers.ETag?.Tag ?? item.ETag;

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        // Suppress local watcher events for this specific file during the write so
        // hydration is never seen as a user-initiated change/delete (which would
        // otherwise queue destructive remote operations).
        _suppressor.Suppress(localPath);
        try
        {
            // Write directly to the target file (truncate-and-write, no delete + move).
            // FileMode.Create truncates if exists, no separate File.Delete needed -
            // so the watcher never sees a Deleted event for the placeholder.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (var ws = new FileStream(localPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read))
                    using (var rs = await resp.Content.ReadAsStreamAsync(ct))
                    {
                        await rs.CopyToAsync(ws, 64 * 1024, ct);
                    }
                    break;
                }
                catch (IOException) when (attempt < 9)
                {
                    await Task.Delay(150, ct);
                }
            }

            // Verify the bytes we wrote match what metadata claimed. A network
            // drop or truncated transfer would otherwise silently leave the
            // file marked as fully hydrated but holding incomplete content,
            // which the user reads as silent data corruption.
            if (item.Size > 0)
            {
                long actualSize;
                try { actualSize = new FileInfo(localPath).Length; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Hydration size check failed (could not stat) for {Path}", localPath);
                    return false;
                }
                if (actualSize != item.Size)
                {
                    _logger.Warning(
                        "Hydration size mismatch for {Drive}:{Path}: expected {Expected} bytes, got {Actual} — discarding partial download",
                        drive.Letter, item.RelativePath, item.Size, actualSize);
                    try { File.Delete(localPath); } catch { /* best effort */ }
                    try { SyncStateMarker.Mark(localPath, SyncOverlayState.Error); } catch { }
                    return false;
                }
            }

            if (item.LastModifiedDateTime != default)
                File.SetLastWriteTime(localPath, item.LastModifiedDateTime);
            if (item.CreatedDateTime != default)
                File.SetCreationTime(localPath, item.CreatedDateTime);

            _metadata.MarkHydrated(drive.ConfigId, item.RelativePath, hydratedETag);
            SyncStateMarker.Mark(localPath, SyncOverlayState.Synced);
            _logger.Information("Hydration complete: {Drive}:{Path} (eTag {ETag})",
                drive.Letter, item.RelativePath, hydratedETag ?? "(none)");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Hydration write failed for {Path}", localPath);
            return false;
        }
        finally
        {
            // Keep events suppressed for a few more seconds while the watcher
            // drains any debounced Changed events from the write.
            _ = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ContinueWith(_ => _suppressor.Release(localPath));
        }
    }

    private static string BuildItemContentUrl(DriveConfig drive, RemoteItem item)
    {
        if (!string.IsNullOrEmpty(item.RemoteItemId))
        {
            if (drive.IsOneDrive)
                return $"https://graph.microsoft.com/v1.0/me/drive/items/{item.RemoteItemId}/content";
            return $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/items/{item.RemoteItemId}/content";
        }

        var rel = item.RelativePath.TrimStart('/');
        if (drive.IsOneDrive)
            return $"https://graph.microsoft.com/v1.0/me/drive/root:/{Uri.EscapeDataString(rel).Replace("%2F", "/")}:/content";
        return $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/root:/{Uri.EscapeDataString(rel).Replace("%2F", "/")}:/content";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    private static string? ExtractServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return Truncate(body, 200);
    }

    public bool EnumerateFolderIfEmpty(DriveConfig drive, string folderRelativePath)
    {
        if (!LazyFallbackEnabled) return false;

        var key = $"{drive.ConfigId}::{folderRelativePath}";

        // Fast path: already enumerated this process — skip without touching disk.
        if (_enumeratedFolders.ContainsKey(key))
            return false;

        // Persistent path: have we successfully enumerated, or recently confirmed
        // not-found, in a previous session? If so, suppress the Graph call.
        var prior = _metadata.GetEnumerationState(drive.ConfigId, folderRelativePath);
        if (prior != null)
        {
            if (prior.LastStatusCode == 200)
            {
                // Self-heal: previous enumeration succeeded and the items are in
                // metadata, but the local backing folder may have been wiped (or only
                // partially rebuilt). If disk has fewer entries than metadata expects,
                // materialise the missing placeholders from existing metadata — no
                // Graph call needed. Catches the case where the eager rebuild is
                // disabled (background drives) and a folder was partially populated
                // before the rebuild was interrupted.
                if (MaterializeMissingPlaceholdersIfNeeded(drive, folderRelativePath))
                {
                    _enumeratedFolders.TryAdd(key, 0);
                    return true;  // we did real work; tell caller to rescan
                }
                // Disk is consistent with metadata (or root path skipped); trust state.
                _enumeratedFolders.TryAdd(key, 0);
                _logger.Debug("Enumeration skipped (prior success cached): {Drive}:{Path}",
                    drive.Letter, folderRelativePath);
                return false;
            }
            if (prior.LastStatusCode == 404 && DateTime.UtcNow < prior.NegativeCacheExpiresUtc)
            {
                // Recently confirmed remote-not-found; honour the TTL.
                _enumeratedFolders.TryAdd(key, 0);
                _logger.Debug("Enumeration skipped (404 cached): {Drive}:{Path}",
                    drive.Letter, folderRelativePath);
                return false;
            }
        }

        // Prefix check: if ANY ancestor is 404-cached, this child must also be
        // missing remotely. This is what kills the PS-Modules / node_modules /
        // .git storm on the FIRST walk - one 404 at the top suppresses thousands
        // of doomed Graph calls for sub-trees.
        var ancestor = FindAncestor404(drive.ConfigId, folderRelativePath);
        if (ancestor != null)
        {
            _logger.Debug("Enumeration skipped (ancestor 404): {Drive}:{Path} (ancestor: {Ancestor})",
                drive.Letter, folderRelativePath, ancestor);
            _metadata.RecordEnumerationNotFound(
                drive.ConfigId, folderRelativePath, NotFoundCacheTtl);
            _enumeratedFolders.TryAdd(key, 0);
            return false;
        }

        // If Graph is currently in cooldown, return immediately with whatever's
        // already on disk rather than blocking Explorer's FindFiles thread for
        // up to MaxRetryAfterSeconds. The folder will be enumerated either by
        // the next DeltaPoller pass or by a subsequent browse after cooldown.
        if (_graph.IsInCooldown)
        {
            _logger.Debug("Skipping on-demand enumeration for {Drive}:{Path} - Graph in cooldown",
                drive.Letter, folderRelativePath);
            return false;
        }

        // Reserve the in-memory slot up-front so concurrent FindFiles calls don't
        // both fire the Graph request. If the call fails, we remove it again.
        if (!_enumeratedFolders.TryAdd(key, 0))
            return false;

        // Bounded blocking. The Dokan FindFiles callback is synchronous, so we
        // can't return until we have the listing — but if Graph hangs we'd
        // freeze Explorer for up to MaxRetryAfterSeconds. Cap at OnDemandEnumerationTimeoutSeconds
        // and pass the CT through to the Graph SendAsync calls so they cancel cleanly
        // rather than silently soaking the timeout in C# code.
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(OnDemandEnumerationTimeoutSeconds));
        try
        {
            return EnumerateFolderAsync(drive, folderRelativePath, timeoutCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _logger.Warning(
                "On-demand enumeration timed out after {Seconds}s for {Drive}:{Path} — Explorer will see an empty folder; next browse retries",
                OnDemandEnumerationTimeoutSeconds, drive.Letter, folderRelativePath);
            _enumeratedFolders.TryRemove(key, out _);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "On-demand folder enumeration failed for {Drive}:{Path}",
                drive.Letter, folderRelativePath);
            _enumeratedFolders.TryRemove(key, out _);
            return false;
        }
    }

    /// <summary>
    /// Hard upper bound on how long Dokan's FindFiles will block waiting for
    /// an on-demand Graph enumeration. After this, Explorer sees an empty
    /// folder (the user can refresh to retry). Prevents one slow Graph
    /// response from freezing the shell on many user machines at once.
    /// </summary>
    private const int OnDemandEnumerationTimeoutSeconds = 15;

    /// <summary>If the local backing folder has fewer direct entries than metadata
    /// expects, create the missing placeholders from existing metadata (no Graph
    /// call). Returns true if any placeholders were created, false otherwise.
    /// Handles the root path "/" too — root-level direct children are found by
    /// MetadataStore.GetDirectChildrenByPath's root special case.</summary>
    private bool MaterializeMissingPlaceholdersIfNeeded(DriveConfig drive, string folderRelativePath)
    {
        // Count what's on disk in the folder's backing dir.
        int diskCount;
        try
        {
            var rel = string.IsNullOrEmpty(folderRelativePath) ? "/" : folderRelativePath;
            var subPath = rel == "/" ? "" : rel.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar);
            var localPath = string.IsNullOrEmpty(subPath)
                ? drive.LocalRootPath
                : System.IO.Path.Combine(drive.LocalRootPath, subPath);
            if (!System.IO.Directory.Exists(localPath)) return false;
            diskCount = System.IO.Directory.GetFileSystemEntries(localPath).Length;
        }
        catch
        {
            return false;
        }

        // Compare to metadata's view of direct children.
        var children = _metadata.GetDirectChildrenByPath(drive.ConfigId, folderRelativePath);
        if (children.Count == 0) return false;
        if (diskCount >= children.Count) return false;  // disk has at least what metadata says

        _logger.Information(
            "Self-heal: {Drive}:{Path} has {Disk} local entries but metadata expects {Meta} — materialising missing placeholders from metadata (no Graph call)",
            drive.Letter, folderRelativePath, diskCount, children.Count);

        _suppressor.Suppress(drive.LocalRootPath);
        int created = 0;
        try
        {
            // Folders first so children can be created under them next.
            foreach (var child in children.Where(c => c.IsFolder))
            {
                try { _placeholders.CreateOrUpdate(drive, child); created++; }
                catch (Exception ex) { _logger.Debug(ex, "Materialise (folder) failed for {Path}", child.RelativePath); }
            }
            foreach (var child in children.Where(c => !c.IsFolder))
            {
                try { _placeholders.CreateOrUpdate(drive, child); created++; }
                catch (Exception ex) { _logger.Debug(ex, "Materialise (file) failed for {Path}", child.RelativePath); }
            }
        }
        finally
        {
            _suppressor.Release(drive.LocalRootPath);
        }

        _logger.Information("Self-heal complete: {Drive}:{Path} materialised {Count} placeholders",
            drive.Letter, folderRelativePath, created);
        return true;
    }

    private string? FindAncestor404(string driveConfigId, string folderRelativePath)
    {
        if (string.IsNullOrEmpty(folderRelativePath) || folderRelativePath == "/")
            return null;

        var path = folderRelativePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        while (lastSlash > 0)
        {
            var parent = path.Substring(0, lastSlash);
            var state = _metadata.GetEnumerationState(driveConfigId, parent);
            if (state != null &&
                state.LastStatusCode == 404 &&
                DateTime.UtcNow < state.NegativeCacheExpiresUtc)
            {
                return parent;
            }
            path = parent;
            lastSlash = path.LastIndexOf('/');
        }
        return null;
    }

    private async Task<bool> EnumerateFolderAsync(DriveConfig drive, string folderRelativePath, CancellationToken ct)
    {
        var encodedPath = folderRelativePath.TrimStart('/');
        string url;
        if (string.IsNullOrEmpty(encodedPath))
        {
            url = drive.IsOneDrive
                ? "https://graph.microsoft.com/v1.0/me/drive/root/children"
                : $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/root/children";
        }
        else
        {
            var segments = encodedPath.Split('/').Select(Uri.EscapeDataString);
            var escaped = string.Join("/", segments);
            url = drive.IsOneDrive
                ? $"https://graph.microsoft.com/v1.0/me/drive/root:/{escaped}:/children"
                : $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/root:/{escaped}:/children";
        }

        _logger.Information("On-demand folder enumeration: {Drive}:{Path}", drive.Letter, folderRelativePath);

        int created = 0;
        bool succeeded = false;
        var newCount = Interlocked.Increment(ref _activeEnumerations);
        try { ActiveEnumerationsChanged?.Invoke(newCount); } catch { /* tray listener — never let it crash a Graph call */ }
        _suppressor.Suppress(drive.LocalRootPath);
        try
        {
            while (!string.IsNullOrEmpty(url))
            {
                ct.ThrowIfCancellationRequested();
                var pageUrl = url; // capture for factory
                using var resp = await _graph.SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, pageUrl),
                    ct,
                    HttpCompletionOption.ResponseHeadersRead);

                if (!resp.IsSuccessStatusCode)
                {
                    var body = resp.Content != null ? await resp.Content.ReadAsStringAsync(ct) : "";
                    _logger.Warning("On-demand enumeration {Status} for {Drive}:{Path}: {Body}",
                        (int)resp.StatusCode, drive.Letter, folderRelativePath, Truncate(body, 300));

                    // 404 = remote folder doesn't exist; cache it so subsequent
                    // FindFiles in the same tree don't re-storm Graph.
                    // Examples: PS Modules under Documents, .git folders, node_modules.
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _metadata.RecordEnumerationNotFound(
                            drive.ConfigId, folderRelativePath, NotFoundCacheTtl);
                    }
                    break;
                }

                succeeded = true;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("value", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var parsed = ParseGraphItem(drive, folderRelativePath, item);
                        if (parsed == null) continue;

                        _metadata.Upsert(parsed);
                        _placeholders.CreateOrUpdate(drive, parsed);
                        created++;
                    }
                }

                if (root.TryGetProperty("@odata.nextLink", out var nextLink))
                    url = nextLink.GetString() ?? string.Empty;
                else
                    url = string.Empty;
            }
        }
        finally
        {
            _ = Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)
                .ContinueWith(_ => _suppressor.Release(drive.LocalRootPath));
            var afterCount = Interlocked.Decrement(ref _activeEnumerations);
            try { ActiveEnumerationsChanged?.Invoke(afterCount); } catch { /* see above */ }
        }

        // Only persist the success signal if we actually got a 2xx response.
        // A 404 already wrote its own (negative) record above; clobbering it
        // with "success" would defeat the negative-cache and re-storm Graph on
        // the next walk through this subtree.
        if (succeeded)
        {
            _metadata.RecordEnumerationSuccess(drive.ConfigId, folderRelativePath);
        }

        if (created > 0)
            _logger.Information("On-demand enumeration complete: {Drive}:{Path} — {Count} items",
                drive.Letter, folderRelativePath, created);

        return created > 0;
    }

    private RemoteItem? ParseGraphItem(DriveConfig drive, string parentRelativePath, System.Text.Json.JsonElement item)
    {
        var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) return null;

        var remoteId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
        var isFolder = item.TryGetProperty("folder", out _);
        long size = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

        DateTime createdDt = default, modifiedDt = default;
        if (item.TryGetProperty("createdDateTime", out var cdt))
            DateTime.TryParse(cdt.GetString(), out createdDt);
        if (item.TryGetProperty("lastModifiedDateTime", out var mdt))
            DateTime.TryParse(mdt.GetString(), out modifiedDt);

        string? etag = item.TryGetProperty("eTag", out var et) ? et.GetString() : null;

        string? hash = null;
        if (item.TryGetProperty("file", out var fileNode) &&
            fileNode.TryGetProperty("hashes", out var hashes))
        {
            if (hashes.TryGetProperty("quickXorHash", out var qxh))
                hash = qxh.GetString();
            else if (hashes.TryGetProperty("sha256Hash", out var sha))
                hash = sha.GetString();
        }

        string parentRemoteId = "";
        if (item.TryGetProperty("parentReference", out var p2) &&
            p2.TryGetProperty("id", out var pid))
            parentRemoteId = pid.GetString() ?? "";

        string? webUrl = item.TryGetProperty("webUrl", out var wu) ? wu.GetString() : null;

        var rel = parentRelativePath.TrimEnd('/') + "/" + name;

        return new RemoteItem
        {
            DriveConfigId = drive.ConfigId,
            RemoteItemId = remoteId,
            ParentRemoteItemId = parentRemoteId,
            RelativePath = rel,
            Name = name,
            IsFolder = isFolder,
            Size = size,
            CreatedDateTime = createdDt,
            LastModifiedDateTime = modifiedDt,
            ETag = etag,
            ContentHash = hash,
            WebUrl = webUrl,
        };
    }

    /// <summary>Drop the in-memory enumeration dedup for a single drive. Used by
    /// Force-resync so the next browse re-enumerates rather than honouring the
    /// pre-resync cache.</summary>
    public void ClearEnumerationStateForDrive(string driveConfigId)
    {
        var prefix = driveConfigId + "::";
        var keysToRemove = new List<string>();
        foreach (var kvp in _enumeratedFolders)
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                keysToRemove.Add(kvp.Key);
        foreach (var k in keysToRemove)
            _enumeratedFolders.TryRemove(k, out _);
        _logger.Information("Cleared {Count} in-memory enumeration entries for drive {DriveId}",
            keysToRemove.Count, driveConfigId);
    }

    public void Dispose()
    {
        _hydrationGate?.Dispose();
    }
}
