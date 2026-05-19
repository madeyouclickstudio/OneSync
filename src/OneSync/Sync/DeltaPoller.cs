using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.State;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Periodically polls Microsoft Graph delta endpoint for each mounted drive,
/// updates the metadata store, and creates/updates placeholder files.
/// </summary>
internal sealed class DeltaPoller : IAsyncDisposable
{
    private readonly GraphHttpClient _graph;
    private readonly MetadataStore _metadata;
    private readonly SyncQueue _queue;
    private readonly PlaceholderManager _placeholders;
    private readonly List<DriveConfig> _drives;
    private readonly SyncSettings _settings;
    private readonly ILogger _logger;
    private readonly LocalChangeSuppressor _suppressor;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly Random _jitter = new();

    private PauseStateStore? _pause;
    public void SetPauseStore(PauseStateStore pause) => _pause = pause;

    /// <summary>
    /// Optional predicate set by CooperativePollingService. When non-null and
    /// returns true for a drive, the background poll loop skips it — that drive's
    /// catch-up runs through the reader's cache pull + hourly self-check instead.
    /// Self-check still calls <see cref="PollDriveAsync"/> directly, bypassing this.
    /// </summary>
    public Func<DriveConfig, bool>? ShouldSkipDrive { get; set; }

    public event Action<DriveConfig>? InitialPollStarted;
    public event Action<DriveConfig, int>? InitialPollProgress;
    public event Action<DriveConfig>? InitialPollCompleted;

    public DeltaPoller(
        GraphHttpClient graph,
        MetadataStore metadata,
        SyncQueue queue,
        PlaceholderManager placeholders,
        IEnumerable<DriveConfig> drives,
        SyncSettings settings,
        LocalChangeSuppressor suppressor,
        ILogger logger)
    {
        _graph = graph;
        _metadata = metadata;
        _queue = queue;
        _placeholders = placeholders;
        _drives = drives.ToList();
        _settings = settings;
        _suppressor = suppressor;
        _logger = logger.ForContext("Component", "DeltaPoller");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Brief startup delay so we don't race the placeholder rebuild and the
        // first user-triggered FindFiles call. Without this we routinely got
        // throttled within 8-23s of process start because delta + on-demand
        // enumeration + placeholder rebuild all hit Graph at once.
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
        catch (OperationCanceledException) { return; }

        // First pass immediately
        try { await RunOnceAsync(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.Warning(ex, "Initial delta pass failed"); }

        var interval = TimeSpan.FromSeconds(Math.Max(60, _settings.DeltaQueryIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                if (_pause?.IsPaused() == true)
                {
                    _logger.Debug("Delta poll skipped (paused)");
                    continue;
                }
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.Warning(ex, "Periodic delta pass failed"); }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        bool first = true;
        foreach (var drive in _drives)
        {
            if (ShouldSkipDrive is not null && ShouldSkipDrive(drive))
            {
                _logger.Debug("Skipping per-user delta poll for {Letter}: (cooperative-polling Reader)", drive.Letter);
                continue;
            }
            // Stagger drives by 5-10s so a 3-drive config doesn't fire three
            // simultaneous /delta requests at startup (which used to cost us a
            // 429 within seconds of launch on the second/third drive).
            if (!first)
            {
                var stagger = TimeSpan.FromMilliseconds(_jitter.Next(5000, 10000));
                try { await Task.Delay(stagger, ct); }
                catch (OperationCanceledException) { throw; }
            }
            first = false;

            try
            {
                await PollDriveAsync(drive, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Delta poll failed for {Letter}: ({Label})",
                    drive.Letter, drive.Label);
            }
        }
    }

    /// <summary>
    /// Run a delta poll for a single drive. Public so cooperative-polling
    /// readers can call it from their self-check loop without going through
    /// the all-drives background pass.
    /// </summary>
    public async Task PollDriveAsync(DriveConfig drive, CancellationToken ct)
    {
        var existingToken = _queue.GetDeltaToken(drive.ConfigId);
        var checkpoint = _queue.GetDeltaCheckpoint(drive.ConfigId);
        bool isInitial = string.IsNullOrEmpty(existingToken);

        string url;
        if (!string.IsNullOrEmpty(checkpoint))
        {
            url = checkpoint;
            _logger.Information("Delta polling {Letter}: {Label} (resuming from checkpoint)",
                drive.Letter, drive.Label);
        }
        else
        {
            url = isInitial ? BuildInitialDeltaUrl(drive) : existingToken!;
            _logger.Information("Delta polling {Letter}: {Label} ({Mode})",
                drive.Letter, drive.Label, isInitial ? "initial" : "incremental");
        }

        if (isInitial && string.IsNullOrEmpty(checkpoint)) InitialPollStarted?.Invoke(drive);

        int created = 0, updated = 0, deleted = 0;
        string? newDeltaLink = null;

        // Suppress local watcher events while we create placeholders
        _suppressor.Suppress(drive.LocalRootPath);

        try
        {
            while (!string.IsNullOrEmpty(url))
            {
                ct.ThrowIfCancellationRequested();

                var pageUrl = url; // capture for the factory lambda
                using var resp = await _graph.SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, pageUrl),
                    ct, HttpCompletionOption.ResponseHeadersRead);
                if (resp.StatusCode == System.Net.HttpStatusCode.Gone)
                {
                    _logger.Warning("Delta token expired (410) for {Letter}: - resetting", drive.Letter);
                    _queue.ClearDeltaToken(drive.ConfigId);
                    _queue.ClearDeltaCheckpoint(drive.ConfigId);
                    // Drop cached enumeration state too - the server's full
                    // re-sync may turn previously-404 folders into 200s.
                    _metadata.ClearEnumerationStateForDrive(drive.ConfigId);
                    return;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.Warning("Delta {Status} for {Letter}: — saved checkpoint, will resume next cycle. {Body}",
                        (int)resp.StatusCode, drive.Letter, Truncate(body, 300));
                    return;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("value", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var (action, _) = ProcessItem(drive, item);
                        switch (action)
                        {
                            case ProcessAction.Created: created++; break;
                            case ProcessAction.Updated: updated++; break;
                            case ProcessAction.Deleted: deleted++; break;
                        }
                    }

                    if (isInitial)
                    {
                        var total = created + updated + deleted;
                        if (total > 0) InitialPollProgress?.Invoke(drive, total);
                    }
                }

                if (root.TryGetProperty("@odata.nextLink", out var nextLink))
                {
                    url = nextLink.GetString() ?? string.Empty;
                    _queue.SetDeltaCheckpoint(drive.ConfigId, url);
                }
                else
                {
                    url = string.Empty;
                }

                if (root.TryGetProperty("@odata.deltaLink", out var deltaLink))
                    newDeltaLink = deltaLink.GetString();
            }

            if (!string.IsNullOrEmpty(newDeltaLink))
                _queue.SetDeltaToken(drive.ConfigId, newDeltaLink);

            _queue.ClearDeltaCheckpoint(drive.ConfigId);

            _logger.Information(
                "Delta {Letter}: complete - created {C} updated {U} deleted {D} (total in store: {Total})",
                drive.Letter, created, updated, deleted, _metadata.CountFor(drive.ConfigId));

            if (isInitial) InitialPollCompleted?.Invoke(drive);
        }
        finally
        {
            // Brief delay so the watcher's debounce cycle absorbs the placeholder creates,
            // then re-enable watching.
            _ = Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)
                .ContinueWith(_ => _suppressor.Release(drive.LocalRootPath));
        }
    }

    private enum ProcessAction { None, Created, Updated, Deleted }

    private (ProcessAction action, string? path) ProcessItem(DriveConfig drive, JsonElement item)
    {
        try
        {
            var remoteId = item.GetProperty("id").GetString() ?? "";
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

            // Deleted facet
            if (item.TryGetProperty("deleted", out _))
            {
                var existing = _metadata.GetById(remoteId);
                if (existing != null)
                {
                    _metadata.AddDeleted(new DeletedItem
                    {
                        RemoteItemId = existing.RemoteItemId,
                        DriveConfigId = drive.ConfigId,
                        DriveLetter = drive.Letter,
                        RelativePath = existing.RelativePath,
                        Name = existing.Name,
                        IsFolder = existing.IsFolder,
                        Size = existing.Size,
                        DeletedAtUtc = DateTime.UtcNow,
                        LastModifiedDateTime = existing.LastModifiedDateTime,
                    });
                    _placeholders.RemovePlaceholder(drive, existing.RelativePath, existing.IsFolder);
                    _metadata.Delete(drive.ConfigId, existing.RelativePath);
                    return (ProcessAction.Deleted, existing.RelativePath);
                }
                return (ProcessAction.None, null);
            }

            // Skip the root folder itself
            if (item.TryGetProperty("root", out _) && name == "")
                return (ProcessAction.None, null);

            // Build the relative path
            string parentPath = "/";
            if (item.TryGetProperty("parentReference", out var parentRef))
            {
                if (parentRef.TryGetProperty("path", out var p))
                {
                    // p is like "/drive/root:/Documents"
                    var raw = p.GetString() ?? "";
                    var rootMarker = "/root:";
                    var idx = raw.IndexOf(rootMarker);
                    if (idx >= 0) parentPath = raw[(idx + rootMarker.Length)..];
                    if (string.IsNullOrEmpty(parentPath)) parentPath = "/";
                }
            }

            var rel = parentPath.TrimEnd('/') + "/" + name;
            rel = PathUtil.NormalizeRelative(rel);
            // Decode URL-encoded characters in the path (Graph returns escaped)
            rel = Uri.UnescapeDataString(rel);

            if (rel.Contains("/$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                rel.StartsWith("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
                return (ProcessAction.None, null);

            var isFolder = item.TryGetProperty("folder", out _);
            long size = 0;
            if (item.TryGetProperty("size", out var sz)) size = sz.GetInt64();

            DateTime created = default, modified = default;
            if (item.TryGetProperty("createdDateTime", out var cdt))
                DateTime.TryParse(cdt.GetString(), out created);
            if (item.TryGetProperty("lastModifiedDateTime", out var mdt))
                DateTime.TryParse(mdt.GetString(), out modified);

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

            // webUrl is needed by OfficeLauncher to route Office opens through
            // `ms-word:ofe|u|<url>` so co-authoring and AutoSave activate.
            string? webUrl = item.TryGetProperty("webUrl", out var wu) ? wu.GetString() : null;

            var existingItem = _metadata.Get(drive.ConfigId, rel);
            var isNew = existingItem == null;

            var newItem = new RemoteItem
            {
                DriveConfigId = drive.ConfigId,
                RemoteItemId = remoteId,
                ParentRemoteItemId = parentRemoteId,
                RelativePath = rel,
                Name = name,
                IsFolder = isFolder,
                Size = size,
                CreatedDateTime = created,
                LastModifiedDateTime = modified,
                ETag = etag,
                ContentHash = hash,
                WebUrl = webUrl,
                // If etag changed, mark for re-hydration
                Hydrated = existingItem?.ETag == etag && existingItem?.Hydrated == true,
            };
            _metadata.Upsert(newItem);

            // Create or update the placeholder
            _placeholders.CreateOrUpdate(drive, newItem);

            return (isNew ? ProcessAction.Created : ProcessAction.Updated, rel);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not process delta item");
            return (ProcessAction.None, null);
        }
    }

    private static string BuildInitialDeltaUrl(DriveConfig drive)
    {
        // ?token=latest tells Graph "skip the full enumeration, just give me the
        // current state token". The response is ~50 bytes (no items, just the
        // @odata.deltaLink). Subsequent polls use the saved token and return only
        // changes since.
        //
        // Effect: a fresh install of a 460k-item library hits Graph ONCE on
        // startup instead of ~2,300 times (one per /delta page). Metadata is
        // built lazily as the user navigates folders (HydrationService's
        // lazy-fallback fires on first browse, fetches /children for that folder
        // only). Aligns with the "only fetch what the user asks for" design.
        //
        // Trade-off: items the user never visits never enter metadata. That's
        // fine — they'd never be hydrated either, and delta tracking catches
        // changes to whatever IS in metadata. Items added remotely to unvisited
        // folders appear when the user eventually visits that folder via
        // lazy-fallback.
        if (drive.IsOneDrive)
            return "https://graph.microsoft.com/v1.0/me/drive/root/delta?token=latest";
        return $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}/root/delta?token=latest";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_pollTask != null)
        {
            try { await _pollTask; } catch { }
        }
        _cts?.Dispose();
    }
}

/// <summary>
/// Shared flag set so that LocalWatcher events fired during delta-driven
/// placeholder creation are not queued as outbound uploads (which would
/// erase the remote content with empty placeholders).
/// </summary>
internal sealed class LocalChangeSuppressor
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _suppressed = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSuppressed(string fullPath)
    {
        foreach (var kvp in _suppressed)
        {
            if (kvp.Value > 0 &&
                fullPath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Suppress(string root)
    {
        _suppressed.AddOrUpdate(root, 1, (_, v) => v + 1);
    }

    public void Release(string root)
    {
        _suppressed.AddOrUpdate(root, 0, (_, v) => Math.Max(0, v - 1));
    }
}
