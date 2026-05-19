using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.State;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync.Cooperative;

/// <summary>
/// Orchestrates the cooperative-polling lifecycle for a set of drives:
///   - Periodically renews each Leader's lock + writes a fresh delta cache.
///   - Periodically (next phase) pulls the leader's cache for Readers.
///   - Releases all locks on shutdown.
/// </summary>
internal sealed class CooperativePollingService : IAsyncDisposable
{
    private readonly List<DriveContext> _drives = new();
    private readonly CooperativePollingConfig _config;
    private readonly string _leaderUserId;
    private readonly string _leaderEmail;
    private readonly string _leaderMachine;
    private readonly string _leaderVersion;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _selfCheckTask;

    private PauseStateStore? _pause;
    public void SetPauseStore(PauseStateStore pause) => _pause = pause;

    public CooperativePollingService(
        CooperativePollingConfig config,
        string leaderUserId,
        string leaderEmail,
        ILogger logger)
    {
        _config = config;
        _leaderUserId = leaderUserId;
        _leaderEmail = leaderEmail;
        _leaderMachine = Environment.MachineName;
        _leaderVersion = typeof(CooperativePollingService).Assembly.GetName().Version?.ToString() ?? "unknown";
        _logger = logger.ForContext("Component", "CooperativePollingService");
    }

    public void RegisterDrive(LeaderElection election, DeltaCache cache, DriveConfig drive, SyncEngine syncEngine)
    {
        _drives.Add(new DriveContext(election, cache, drive, syncEngine));
    }

    public void Start()
    {
        if (!_config.Enabled || _drives.Count == 0) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        _selfCheckTask = Task.Run(() => RunSelfCheckLoopAsync(_cts.Token));
        _logger.Information(
            "CooperativePollingService started ({DriveCount} drives, renew every {Renew}s, self-check every {Check}min)",
            _drives.Count, _config.RenewIntervalSeconds, _config.SelfCheckIntervalMinutes);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _config.RenewIntervalSeconds));
        // First tick happens immediately so the cache file appears right after election.
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!first)
                {
                    await Task.Delay(interval, ct);
                }
                first = false;

                foreach (var dc in _drives)
                {
                    try { await TickDriveAsync(dc, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Tick failed for {Letter}:", dc.Drive.Letter);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "CooperativePollingService loop error");
            }
        }
    }

    private async Task TickDriveAsync(DriveContext dc, CancellationToken ct)
    {
        if (dc.Election.Role == LeaderRole.Leader)
        {
            var stillLeader = await dc.Election.RenewAsync(ct);
            if (stillLeader)
            {
                if (_pause?.IsPaused() == true)
                {
                    _logger.Debug("Cache write skipped for {Letter}: (paused)", dc.Drive.Letter);
                }
                else
                {
                    // Write a fresh delta cache from the leader's local metadata view —
                    // but only if the content has changed since last write, and only the
                    // sliding window of recent files (plus all folders for navigation).
                    var items = dc.SyncEngine.Metadata.GetForDrive(dc.Drive.ConfigId);
                    var deltaToken = dc.SyncEngine.Queue.GetDeltaToken(dc.Drive.ConfigId);
                    var payload = DeltaCache.BuildPayload(
                        _leaderUserId, _leaderEmail, _leaderMachine, _leaderVersion,
                        deltaToken, items, _config.CacheItemMaxAgeDays);
                    await dc.Cache.WriteIfChangedAsync(payload, _config.CacheForceWriteEveryNCycles, ct);
                }
            }
            else
            {
                // RenewAsync demoted us (412 or stale-renewal threshold). Fall
                // through to the post-leader path below — without this, the
                // demoted machine would sit blind until the next tick (up to
                // RenewIntervalSeconds, default 5 min). This recovers in the
                // same tick instead.
                _logger.Information(
                    "Lost leadership for {Letter}: — falling through to {Role} path this cycle to avoid blind window",
                    dc.Drive.Letter, dc.Election.Role);
            }
        }

        // Reader path. Also runs alongside the leader when the dev flag is set, so a
        // single-user build can exercise both code paths.
        var runReader = dc.Election.Role == LeaderRole.Reader
                        || (_config.ForceReaderRoleAlongsideLeader && dc.Election.Role == LeaderRole.Leader);
        if (runReader)
        {
            await ReadAndMergeAsync(dc, ct);
        }
    }

    private async Task ReadAndMergeAsync(DriveContext dc, CancellationToken ct)
    {
        var payload = await dc.Cache.ReadAsync(ct);
        if (payload is null)
        {
            _logger.Debug("Reader: no delta cache present for {Letter}: (leader hasn't written yet)", dc.Drive.Letter);
            return;
        }

        var metadata = dc.SyncEngine.Metadata;
        var placeholders = dc.SyncEngine.Placeholders;
        var suppressor = dc.SyncEngine.Suppressor;

        int created = 0, updated = 0, skipped = 0;
        suppressor.Suppress(dc.Drive.LocalRootPath);
        try
        {
            foreach (var ci in payload.Items)
            {
                var key = RemoteItem.MakeKey(dc.Drive.ConfigId, ci.Path);
                var existing = metadata.Get(dc.Drive.ConfigId, ci.Path);

                if (existing is null)
                {
                    var fresh = new RemoteItem
                    {
                        Key = key,
                        DriveConfigId = dc.Drive.ConfigId,
                        RemoteItemId = ci.Id,
                        ParentRemoteItemId = ci.ParentId,
                        RelativePath = ci.Path,
                        Name = ci.Name,
                        IsFolder = ci.IsFolder,
                        Size = ci.Size,
                        ETag = ci.ETag,
                        WebUrl = ci.WebUrl,
                        LastModifiedDateTime = ci.LastModified,
                        LastSyncedAt = DateTime.UtcNow,
                    };
                    metadata.Upsert(fresh);
                    try { placeholders.CreateOrUpdate(dc.Drive, fresh); }
                    catch (Exception ex) { _logger.Debug(ex, "Placeholder create failed for {Path}", ci.Path); }
                    created++;
                }
                else if (existing.ETag != ci.ETag || existing.Size != ci.Size)
                {
                    // Copy-on-write rather than mutating the existing instance in
                    // place. A concurrent DeltaPoller / HydrationService can hold
                    // a reference to the same object; mutating it field-by-field
                    // would publish torn intermediate state to those readers.
                    // Preserves fields that aren't replicated through the cache
                    // (Hydrated, LastAccessedAt, PlaceholderCreated, etc.).
                    var updatedItem = new RemoteItem
                    {
                        Key = existing.Key,
                        DriveConfigId = existing.DriveConfigId,
                        RemoteItemId = ci.Id,
                        ParentRemoteItemId = ci.ParentId,
                        RelativePath = existing.RelativePath,
                        Name = ci.Name,
                        IsFolder = existing.IsFolder,
                        Size = ci.Size,
                        ETag = ci.ETag,
                        WebUrl = ci.WebUrl,
                        ContentHash = existing.ContentHash,
                        LastModifiedDateTime = ci.LastModified,
                        CreatedDateTime = existing.CreatedDateTime,
                        Hydrated = existing.Hydrated,
                        PlaceholderCreated = existing.PlaceholderCreated,
                        LastSyncedAt = DateTime.UtcNow,
                        LastAccessedAt = existing.LastAccessedAt,
                    };
                    metadata.Upsert(updatedItem);
                    try { placeholders.CreateOrUpdate(dc.Drive, updatedItem); }
                    catch (Exception ex) { _logger.Debug(ex, "Placeholder update failed for {Path}", ci.Path); }
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
        }
        finally
        {
            suppressor.Release(dc.Drive.LocalRootPath);
        }

        _logger.Information(
            "Reader merged delta cache for {Letter}: {Created} new, {Updated} changed, {Skipped} unchanged (cache had {Total} items, leader: {Leader})",
            dc.Drive.Letter, created, updated, skipped, payload.Items.Count, payload.LeaderEmail);

        // After the FIRST cache merge as a Reader, check whether this user has
        // broader permissions than the leader (e.g. HR staff with access to
        // folders the non-HR leader can't see). If so, fire a one-shot delta
        // poll so the extras appear immediately instead of waiting up to an
        // hour for the next self-check.
        if (!dc.InitialDivergenceChecked && dc.Election.Role == LeaderRole.Reader)
        {
            dc.InitialDivergenceChecked = true;
            await CheckInitialDivergenceAsync(dc, payload, ct);
        }
    }

    private async Task CheckInitialDivergenceAsync(DriveContext dc, DeltaCachePayload payload, CancellationToken ct)
    {
        var readerFolders = dc.Election.KnownTopLevelFolders;
        if (readerFolders.Count == 0)
        {
            _logger.Debug("No top-level folders from probe for {Letter}: — skipping divergence check", dc.Drive.Letter);
            return;
        }

        // A top-level item in the cache has a path with exactly one slash,
        // before the name (e.g. "/Maths"). Deeper items have more slashes.
        var cacheTopFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in payload.Items)
        {
            if (!item.IsFolder) continue;
            if (string.IsNullOrEmpty(item.Id)) continue;
            var p = item.Path;
            if (string.IsNullOrEmpty(p) || p[0] != '/') continue;
            if (p.IndexOf('/', 1) >= 0) continue;
            cacheTopFolderIds.Add(item.Id);
        }

        // Extras = top-level folders this reader can see but the leader's cache doesn't cover.
        var extras = new List<(string Id, string Name)>();
        foreach (var kvp in readerFolders)
            if (!cacheTopFolderIds.Contains(kvp.Key))
                extras.Add((kvp.Key, kvp.Value));

        if (extras.Count == 0)
        {
            _logger.Debug(
                "Reader's top-level view matches leader's cache for {Letter}: ({Count} folders) — no divergence",
                dc.Drive.Letter, readerFolders.Count);
            return;
        }

        _logger.Information(
            "Reader has {Extras} top-level folder(s) not in leader's cache for {Letter}: — enumerating just those folders (reader sees {Reader}, cache sees {Cache})",
            extras.Count, dc.Drive.Letter, readerFolders.Count, cacheTopFolderIds.Count);

        // Targeted enumeration. For each divergent folder, ask HydrationService to
        // enumerate its immediate children — typically one Graph call per folder.
        // Deeper levels (HR/Payroll/2024/, etc.) populate on demand via the
        // existing lazy-fallback path when the user actually navigates to them.
        // This is dramatically cheaper than running a full /drive/root/delta
        // for a first-time reader who has no delta token yet.
        int enumerated = 0;
        foreach (var (id, name) in extras)
        {
            if (string.IsNullOrEmpty(name)) continue;
            ct.ThrowIfCancellationRequested();
            try
            {
                var rel = "/" + name;
                var fired = await Task.Run(
                    () => dc.SyncEngine.Hydration.EnumerateFolderIfEmpty(dc.Drive, rel), ct);
                if (fired) enumerated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Targeted enumeration failed for {Letter}:{Folder}", dc.Drive.Letter, name);
            }
        }

        _logger.Information(
            "Divergence enumeration complete for {Letter}: {Enumerated}/{Total} folders surfaced (deeper levels lazy-load on Explorer navigation)",
            dc.Drive.Letter, enumerated, extras.Count);
    }

    /// <summary>
    /// Periodic delta-query self-check that catches items the leader's cache
    /// couldn't see (permission islands). Cheap — uses a delta token so each
    /// run returns only what's changed since last poll. Runs much less often
    /// than the renew loop so the total Graph cost stays small.
    /// </summary>
    private async Task RunSelfCheckLoopAsync(CancellationToken ct)
    {
        // Stagger the first run by half the interval so a fleet of clients
        // doesn't all hit Graph at the same wall-clock minute.
        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.SelfCheckIntervalMinutes));
        var initialDelay = TimeSpan.FromSeconds(_jitter.Next(30, (int)Math.Max(60, interval.TotalSeconds / 2)));
        try { await Task.Delay(initialDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var dc in _drives)
                {
                    var shouldSelfCheck = dc.Election.Role == LeaderRole.Reader
                        || dc.Election.Role == LeaderRole.NotParticipating
                        || _config.ForceReaderRoleAlongsideLeader;
                    if (!shouldSelfCheck) continue;

                    try { await SelfCheckDriveAsync(dc, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Self-check failed for {Letter}:", dc.Drive.Letter);
                    }
                }
            }
            catch (OperationCanceledException) { return; }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SelfCheckDriveAsync(DriveContext dc, CancellationToken ct)
    {
        // Trigger the per-user delta poller for THIS drive only, bypassing the
        // skip predicate that suppresses Readers in the background loop. Items
        // discovered get Upsert'd + placeholder-created automatically.
        var preCount = dc.SyncEngine.Metadata.GetForDrive(dc.Drive.ConfigId).Count;
        await dc.SyncEngine.DeltaPoller.PollDriveAsync(dc.Drive, ct);
        var postCount = dc.SyncEngine.Metadata.GetForDrive(dc.Drive.ConfigId).Count;
        _logger.Information(
            "Self-check completed for {Letter}: (metadata items {Before} → {After}, delta {Delta:+0;-0;0})",
            dc.Drive.Letter, preCount, postCount, postCount - preCount);
    }

    /// <summary>
    /// Sets DeltaPoller's skip predicate so it bypasses Reader drives in the
    /// background loop. Called by Program.cs after election completes.
    /// </summary>
    public void WireUpDeltaPollerSkip(DeltaPoller poller)
    {
        poller.ShouldSkipDrive = drive =>
        {
            foreach (var dc in _drives)
            {
                if (dc.Drive.ConfigId != drive.ConfigId) continue;
                // Reader = leader is writing the cache for us; skip our own poll.
                // NotParticipating = either coop is disabled for this drive or
                // we're permission-limited without a current leader — fall back
                // to per-user polling so we don't go blind.
                return dc.Election.Role == LeaderRole.Reader;
            }
            return false;
        };
    }

    private readonly Random _jitter = new();

    public async Task RelinquishAllAsync()
    {
        foreach (var dc in _drives)
        {
            try { await dc.Election.RelinquishAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Relinquish failed for {Letter}:", dc.Drive.Letter); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            foreach (var task in new[] { _loopTask, _selfCheckTask })
            {
                if (task is null) continue;
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* swallow */ }
            }
        }
        finally
        {
            _cts?.Dispose();
        }
    }

    private sealed class DriveContext
    {
        public LeaderElection Election { get; }
        public DeltaCache Cache { get; }
        public DriveConfig Drive { get; }
        public SyncEngine SyncEngine { get; }
        public bool InitialDivergenceChecked { get; set; }

        public DriveContext(LeaderElection election, DeltaCache cache, DriveConfig drive, SyncEngine syncEngine)
        {
            Election = election;
            Cache = cache;
            Drive = drive;
            SyncEngine = syncEngine;
        }
    }
}
