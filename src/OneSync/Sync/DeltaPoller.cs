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
    /// returns true for a drive, the background poll loop skips it - that drive's
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
                    _logger.Warning("Delta {Status} for {Letter}: - saved checkpoint, will resume next cycle. {Body}",
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Delta poll for {Letter}: stopped part-way; resumes from checkpoint", drive.Letter);
        }
    }

    private enum ProcessAction { None, Created, Updated, Deleted }

    private (ProcessAction action, string? path) ProcessItem(DriveConfig drive, JsonElement item)
    {
        try
        {
            var remoteId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";

            if (item.TryGetProperty("deleted", out _))
            {
                var existing = _metadata.GetById(remoteId);
                if (existing == null || existing.DriveConfigId != drive.ConfigId)
                    return (ProcessAction.None, null);

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

                // Local edits under a deleted item are kept and sent up again as new files.
                foreach (var keptPath in _placeholders.RemovePlaceholder(drive, existing.RelativePath, existing.IsFolder))
                {
                    _queue.Enqueue(new SyncOperation
                    {
                        Type = SyncOpType.Upload,
                        DriveConfigId = drive.ConfigId,
                        DriveLetter = drive.Letter,
                        RelativePath = keptPath,
                        Priority = drive.Priority,
                    });
                }
                return (ProcessAction.Deleted, existing.RelativePath);
            }

            var parsed = GraphItem.Parse(drive, item);
            if (parsed == null) return (ProcessAction.None, null);

            // Only follow items we already know, or new ones in folders we're
            // tracking. Everything else is fetched when the user opens its folder.
            var known = _metadata.Get(drive.ConfigId, parsed.RelativePath) ??
                        (string.IsNullOrEmpty(parsed.RemoteItemId) ? null : _metadata.GetById(parsed.RemoteItemId));
            if (known == null && !_placeholders.IsTrackedFolder(drive, PathUtil.ParentOf(parsed.RelativePath)))
                return (ProcessAction.None, null);

            if (_queue.HasPendingForPath(drive.ConfigId, parsed.RelativePath) &&
                _metadata.Get(drive.ConfigId, parsed.RelativePath) is null)
            {
                // A local file of the same name is waiting to upload; the upload
                // works out which copy wins, so leave the local file alone.
                return (ProcessAction.None, null);
            }

            var result = _placeholders.ApplyRemote(drive, parsed);
            return (result == PlaceholderManager.ApplyResult.Created ? ProcessAction.Created : ProcessAction.Updated,
                parsed.RelativePath);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not process delta item");
            return (ProcessAction.None, null);
        }
    }

    private static string BuildInitialDeltaUrl(DriveConfig drive)
    {
        // Start from the current state rather than enumerating the whole library;
        // folders are listed when they're first opened.
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
