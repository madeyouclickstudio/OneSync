using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync.Cooperative;

internal enum LeaderRole
{
    /// <summary>Cooperative polling disabled, drive ineligible, or probe failed.
    /// The drive falls back to per-user delta polling.</summary>
    NotParticipating,

    /// <summary>This client holds the lock and is responsible for writing the delta cache.</summary>
    Leader,

    /// <summary>Another client holds a valid lock; we are a reader of the cache they produce.</summary>
    Reader,
}

/// <summary>
/// Distributed lock for cooperative polling.
///
/// One client per shared library writes the delta cache; everyone else reads it.
/// Coordination is via a small lock file at <c>{controlFolder}/poller.lock</c>
/// inside the library itself, using Graph's ETag-based optimistic concurrency
/// (<c>If-None-Match: *</c> for create, <c>If-Match: {etag}</c> for atomic update).
///
/// Failure modes:
///   - Graceful shutdown: leader deletes the lock; next user claims it instantly.
///   - Ungraceful death: lock contains an <c>expiresAt</c> timestamp; once past,
///     any reader can claim leadership via a conditional PUT.
///   - Permission-limited leader: readers run their own delta self-check at
///     <c>selfCheckIntervalMinutes</c> so items the leader can't see are still
///     picked up by users who can.
/// </summary>
internal sealed class LeaderElection
{
    private const int SchemaVersion = 1;

    private readonly GraphHttpClient _graph;
    private readonly DriveConfig _drive;
    private readonly CooperativePollingConfig _coopConfig;
    private readonly ILogger _logger;
    private readonly string _userId;
    private readonly string _userEmail;
    private readonly string _machineName;
    private readonly string _appVersion;
    private readonly string _driveBaseUrl;
    private readonly string _lockPath;          // e.g. ".onesync-test/poller.lock"
    private readonly string _controlFolder;     // e.g. ".onesync-test"

    private string? _lockEtag;
    private DateTime _lockExpiresAtUtc;
    private DateTime _lastSuccessfulRenewalUtc;
    private readonly Dictionary<string, string> _knownTopLevelFolders = new(StringComparer.OrdinalIgnoreCase);

    public LeaderRole Role { get; private set; } = LeaderRole.NotParticipating;

    /// <summary>
    /// Top-level folders this client saw during its read probe — keyed by
    /// folder ID, valued by name — excluding the control folder. Used by the
    /// reader path to detect permission divergence: if a reader can see folders
    /// that aren't represented in the leader's cache (an HR user with access to
    /// a folder a non-HR leader can't see, etc.), the reader fires a targeted
    /// enumeration of just those folders so they appear immediately instead of
    /// waiting up to an hour for the next self-check.
    /// </summary>
    public IReadOnlyDictionary<string, string> KnownTopLevelFolders => _knownTopLevelFolders;

    public LeaderElection(
        GraphHttpClient graph,
        DriveConfig drive,
        CooperativePollingConfig coopConfig,
        string userId,
        string userEmail,
        ILogger logger)
    {
        _graph = graph;
        _drive = drive;
        _coopConfig = coopConfig;
        _userId = userId;
        _userEmail = userEmail;
        _machineName = Environment.MachineName;
        _appVersion = typeof(LeaderElection).Assembly.GetName().Version?.ToString() ?? "unknown";
        _controlFolder = string.IsNullOrWhiteSpace(coopConfig.ControlFolder) ? ".onesync" : coopConfig.ControlFolder.Trim('/');
        _lockPath = $"{_controlFolder}/poller.lock";
        _logger = logger
            .ForContext("Component", "LeaderElection")
            .ForContext("Drive", $"{drive.Letter}:");

        if (string.IsNullOrEmpty(drive.ResolvedDriveId))
            throw new InvalidOperationException("Drive must be resolved before LeaderElection can run");
        _driveBaseUrl = $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}";
    }

    /// <summary>
    /// Determine whether this client can lead, can only read, or can't participate
    /// at all. Two-stage check: read probe (top-level folders) then write probe
    /// (create the control folder). The write probe is also the upfront permission
    /// check that prevents the noisy "Lock create returned 403" cascade on
    /// read-only users.
    /// </summary>
    public async Task ClaimOrJoinAsync(CancellationToken ct)
    {
        if (!_coopConfig.Enabled)
        {
            _logger.Information("Cooperative polling disabled in config — staying with per-user delta polling");
            Role = LeaderRole.NotParticipating;
            return;
        }

        // 1) Read probe — confirms we can see everything at the top level.
        // A leader with subset visibility writes a subset cache; readers with broader
        // visibility would be missing items. Decline candidacy if subset-visible;
        // the hourly self-check still catches anything for users like that.
        var readOk = await ProbeTopLevelAccessAsync(ct);
        if (!readOk)
        {
            Role = LeaderRole.NotParticipating;
            _logger.Information(
                "Read probe failed — operating on own delta polls until someone eligible claims leadership");
            return;
        }

        // 2) Write probe — try to create the control folder.
        //
        // Cooperative polling is *opt-in by write capability*. A read-only user
        // becoming a Reader of someone else's cache would inherit the leader's
        // view of the library, but read-only and read/write users in the same
        // library do not necessarily see the same content (staff-only subfolders
        // a student leader can't see, etc.). So we restrict the Reader role to
        // clients who could themselves have been leader — guaranteeing the
        // cohort all shares the same permission profile.
        //
        // 201 (created) and 409 (already exists) both confirm write access at the
        // library root. 403 means this user falls back to per-user delta polling.
        var writeOk = await ProbeWriteAccessAsync(ct);
        if (!writeOk)
        {
            Role = LeaderRole.NotParticipating;
            _logger.Information(
                "Read-only at library root — running per-user delta poll for this drive (cooperative polling is opt-in by write capability so readers and the leader share the same permission profile)");
            return;
        }

        // Try to create the lock file with If-None-Match: * (create-only semantics).
        // If it succeeds: we're leader. If 412 Precondition Failed: someone else has it.
        var (claimed, etag) = await TryCreateLockAsync(ct);
        if (claimed)
        {
            _lockEtag = etag;
            _lockExpiresAtUtc = DateTime.UtcNow.AddSeconds(_coopConfig.LeaseTtlSeconds);
            _lastSuccessfulRenewalUtc = DateTime.UtcNow;
            Role = LeaderRole.Leader;
            _logger.Information("Acquired leader role (lease until {Expires:HH:mm:ss} UTC)", _lockExpiresAtUtc);
            return;
        }

        // Someone else got there first OR a lock already exists. Read it.
        var existing = await ReadLockAsync(ct);
        if (existing is null)
        {
            // Lock doesn't exist (deleted between our create attempt and our read) — retry once.
            (claimed, etag) = await TryCreateLockAsync(ct);
            if (claimed)
            {
                _lockEtag = etag;
                _lockExpiresAtUtc = DateTime.UtcNow.AddSeconds(_coopConfig.LeaseTtlSeconds);
                Role = LeaderRole.Leader;
                _logger.Information("Acquired leader role on retry (lease until {Expires:HH:mm:ss} UTC)", _lockExpiresAtUtc);
                return;
            }
            // Still couldn't claim — fall through to reader.
            existing = await ReadLockAsync(ct);
            if (existing is null)
            {
                _logger.Warning("Could not claim or read lock — operating in per-user fallback");
                Role = LeaderRole.NotParticipating;
                return;
            }
        }

        // If the existing lock is ours (restart resilience) or expired, take it over with If-Match.
        var isOwnLock = string.Equals(existing.Value.UserId, _userId, StringComparison.OrdinalIgnoreCase);
        if (isOwnLock || existing.Value.ExpiresAt <= DateTime.UtcNow)
        {
            var tookOver = await TryTakeOverExpiredLockAsync(existing.Value.Etag, ct);
            if (tookOver.success)
            {
                _lockEtag = tookOver.newEtag;
                _lockExpiresAtUtc = DateTime.UtcNow.AddSeconds(_coopConfig.LeaseTtlSeconds);
                _lastSuccessfulRenewalUtc = DateTime.UtcNow;
                Role = LeaderRole.Leader;
                _logger.Information("{Reason} (lease until {Expires:HH:mm:ss} UTC)",
                    isOwnLock ? "Reclaimed own lock after restart" : $"Took over expired lock from {existing.Value.UserEmail}",
                    _lockExpiresAtUtc);
                return;
            }
            // Someone else beat us to the takeover. Fall through to reader.
        }

        Role = LeaderRole.Reader;
        _logger.Information("Reader mode: leader is {Leader} on {Machine}, lease until {Expires:HH:mm:ss} UTC",
            existing.Value.UserEmail, existing.Value.MachineName, existing.Value.ExpiresAt);
    }

    /// <summary>
    /// Refresh the lock (leader only). Returns true if we still hold it after
    /// the refresh, false if we've lost it (412 from another claimant, or we've
    /// been unable to renew for so long that the lease is no longer safely ours).
    /// </summary>
    public async Task<bool> RenewAsync(CancellationToken ct)
    {
        if (Role != LeaderRole.Leader)
            throw new InvalidOperationException("RenewAsync called on non-leader");

        var newExpiry = DateTime.UtcNow.AddSeconds(_coopConfig.LeaseTtlSeconds);
        var body = BuildLockBody(newExpiry);

        var url = $"{_driveBaseUrl}/root:/{_lockPath}:/content";
        using var resp = await _graph.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("If-Match", _lockEtag);
            return req;
        }, ct);

        if (resp.IsSuccessStatusCode)
        {
            _lockEtag = ParseEtagFromResponse(resp);
            _lockExpiresAtUtc = newExpiry;
            _lastSuccessfulRenewalUtc = DateTime.UtcNow;
            _logger.Debug("Lock renewed (lease until {Expires:HH:mm:ss} UTC)", _lockExpiresAtUtc);
            return true;
        }

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            _logger.Warning("Lock renewal rejected (412) — another client overwrote our lock; demoting to reader");
            Role = LeaderRole.Reader;
            _lockEtag = null;
            return false;
        }

        // Defensive demotion: if we've been unable to renew for more than half
        // the lease TTL, the lock could plausibly expire before we get another
        // chance. Stop claiming leadership so we don't risk split brain with a
        // user who legitimately takes over.
        var elapsedSinceLastSuccess = DateTime.UtcNow - _lastSuccessfulRenewalUtc;
        var halfLease = TimeSpan.FromSeconds(_coopConfig.LeaseTtlSeconds / 2.0);
        if (elapsedSinceLastSuccess > halfLease)
        {
            _logger.Warning(
                "Lock renewal returned {Status} and {Elapsed} have passed since last successful renew (≥ half-lease {Half}) — demoting defensively",
                (int)resp.StatusCode, elapsedSinceLastSuccess, halfLease);
            Role = LeaderRole.NotParticipating;
            _lockEtag = null;
            return false;
        }

        _logger.Warning(
            "Lock renewal returned {Status} — still inside safe window ({Elapsed} < half-lease), retrying next cycle",
            (int)resp.StatusCode, elapsedSinceLastSuccess);
        return true;
    }

    /// <summary>
    /// Release the lock (leader only). Best-effort: if delete fails, the expires
    /// timestamp will still allow recovery.
    /// </summary>
    public async Task RelinquishAsync(CancellationToken ct = default)
    {
        if (Role != LeaderRole.Leader) return;

        var url = $"{_driveBaseUrl}/root:/{_lockPath}";
        try
        {
            using var resp = await _graph.SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Delete, url);
                if (!string.IsNullOrEmpty(_lockEtag))
                    req.Headers.TryAddWithoutValidation("If-Match", _lockEtag);
                return req;
            }, ct);

            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound)
                _logger.Information("Released leader lock");
            else
                _logger.Warning("Lock release returned {Status} — relying on expiry", (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Lock release failed — relying on expiry");
        }
        finally
        {
            Role = LeaderRole.NotParticipating;
            _lockEtag = null;
        }
    }

    // ---- Internals ----

    /// <summary>
    /// Probe access to each top-level folder in the library. Returns true if
    /// every top-level folder is readable. A 403 anywhere means the user is
    /// permission-limited and should not lead.
    ///
    /// Probes run concurrently (capped at 8 in flight) with early-fail: a single
    /// 403 cancels the rest. For a library with 200+ top-level folders, this
    /// turns a 30-second serial probe into a few-second parallel one.
    /// </summary>
    private async Task<bool> ProbeTopLevelAccessAsync(CancellationToken ct)
    {
        // Collect probe targets across ALL pages — a library with 201+ top-level
        // folders would otherwise silently miss the rest, possibly declaring an
        // ineligible (permission-limited) user as eligible because only the
        // accessible first 200 were tested.
        var folders = new List<(string Id, string Name)>();
        int totalChildren = 0;
        _knownTopLevelFolders.Clear();

        var pageUrl = $"{_driveBaseUrl}/root/children?$select=id,name,folder&$top=200";
        while (!string.IsNullOrEmpty(pageUrl))
        {
            ct.ThrowIfCancellationRequested();
            var thisPageUrl = pageUrl;
            using var resp = await _graph.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, thisPageUrl), ct);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.Information("Permission probe: root listing returned 403 — not eligible for leadership");
                return false;
            }
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warning("Permission probe: root listing returned {Status} — declining leadership conservatively",
                    (int)resp.StatusCode);
                return false;
            }

            var pageJson = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(pageJson);
            if (!doc.RootElement.TryGetProperty("value", out var values))
            {
                _logger.Warning("Permission probe: root listing missing 'value' array — declining leadership");
                return false;
            }

            foreach (var item in values.EnumerateArray())
            {
                totalChildren++;
                if (!item.TryGetProperty("folder", out _)) continue;
                if (!item.TryGetProperty("id", out var idProp)) continue;
                var folderId = idProp.GetString();
                if (string.IsNullOrEmpty(folderId)) continue;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.Equals(name, _controlFolder, StringComparison.OrdinalIgnoreCase)) continue;
                folders.Add((folderId, name));
                _knownTopLevelFolders[folderId] = name;
            }

            pageUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                ? next.GetString() ?? string.Empty
                : string.Empty;
        }

        if (folders.Count == 0)
        {
            _logger.Information("Permission probe: no top-level folders to probe (root has {Total} items) — eligible",
                totalChildren);
            return true;
        }

        // Parallel probe with early-fail. cts cancels remaining probes when one fails.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(8, 8);
        int probed = 0;
        string? failureFolder = null;
        int failureStatus = 0;

        var tasks = folders.Select(async f =>
        {
            await gate.WaitAsync(probeCts.Token);
            try
            {
                if (probeCts.IsCancellationRequested) return;
                var probeUrl = $"{_driveBaseUrl}/items/{f.Id}/children?$select=id&$top=1";
                using var probe = await _graph.SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, probeUrl), probeCts.Token);
                Interlocked.Increment(ref probed);

                if (probe.StatusCode == HttpStatusCode.Forbidden ||
                    (!probe.IsSuccessStatusCode && probe.StatusCode != HttpStatusCode.NotFound))
                {
                    // Record the first failure and cancel siblings.
                    if (Interlocked.CompareExchange(ref failureStatus, (int)probe.StatusCode, 0) == 0)
                    {
                        failureFolder = f.Name;
                    }
                    probeCts.Cancel();
                }
            }
            catch (OperationCanceledException) { /* fine — sibling failed */ }
            finally { gate.Release(); }
        }).ToArray();

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* expected when sibling cancels */ }

        if (failureStatus != 0)
        {
            if (failureStatus == 403)
            {
                _logger.Information("Permission probe: 403 on top-level folder '{Folder}' — not eligible for leadership",
                    failureFolder);
            }
            else
            {
                _logger.Warning("Permission probe: {Status} on top-level folder '{Folder}' — declining leadership",
                    failureStatus, failureFolder);
            }
            return false;
        }

        _logger.Information(
            "Permission probe: root has {Total} items ({Folders} folders); probed {Probed}/{Target} top-level subfolders in parallel without 403 — eligible",
            totalChildren, folders.Count, probed, folders.Count);
        return true;
    }

    /// <summary>
    /// Probe write access by trying to create the control folder. 201 = created,
    /// 409 = already exists, both confirm write at library root. 403 = read-only.
    /// Idempotent: replaces the old EnsureControlFolderAsync — when this returns
    /// true the folder is guaranteed to exist.
    /// </summary>
    private async Task<bool> ProbeWriteAccessAsync(CancellationToken ct)
    {
        var createUrl = $"{_driveBaseUrl}/root/children";
        var folderBody = $"{{\"name\":\"{_controlFolder}\",\"folder\":{{}},\"@microsoft.graph.conflictBehavior\":\"fail\"}}";

        using var resp = await _graph.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, createUrl)
            {
                Content = new StringContent(folderBody, Encoding.UTF8, "application/json"),
            };
            return req;
        }, ct);

        if (resp.StatusCode == HttpStatusCode.Created)
        {
            _logger.Information("Write probe: created control folder '/{Folder}/' — leader-eligible", _controlFolder);
            return true;
        }
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            // 409 means the folder is there. We have write at root because conflictBehavior=fail
            // means MSI would have 403'd if we lacked permission to attempt the create.
            // (Note: SharePoint can also 403 on the path differently. We'll catch that on lock create.)
            _logger.Debug("Write probe: control folder '/{Folder}/' already exists (409) — leader-eligible", _controlFolder);
            return true;
        }
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.Information(
                "Write probe: 403 creating control folder '/{Folder}/' — this client is read-only at library root, will operate as reader if a leader exists",
                _controlFolder);
            return false;
        }

        _logger.Warning(
            "Write probe: unexpected status {Status} creating control folder '/{Folder}/' — declining leadership defensively",
            (int)resp.StatusCode, _controlFolder);
        return false;
    }

    private async Task<(bool claimed, string? etag)> TryCreateLockAsync(CancellationToken ct)
    {
        var newExpiry = DateTime.UtcNow.AddSeconds(_coopConfig.LeaseTtlSeconds);
        var body = BuildLockBody(newExpiry);

        var url = $"{_driveBaseUrl}/root:/{_lockPath}:/content";
        using var resp = await _graph.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("If-None-Match", "*");
            return req;
        }, ct);

        if (resp.IsSuccessStatusCode)
            return (true, ParseEtagFromResponse(resp));

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
            return (false, null);

        _logger.Warning("Lock create returned {Status}", (int)resp.StatusCode);
        return (false, null);
    }

    private async Task<LockInfo?> ReadLockAsync(CancellationToken ct)
    {
        var url = $"{_driveBaseUrl}/root:/{_lockPath}:/content";
        using var resp = await _graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);

        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Warning("Lock read returned {Status}", (int)resp.StatusCode);
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        var etag = ParseEtagFromResponse(resp);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new LockInfo(
                UserId: root.TryGetProperty("userId", out var u) ? u.GetString() ?? "" : "",
                UserEmail: root.TryGetProperty("userEmail", out var e) ? e.GetString() ?? "" : "",
                MachineName: root.TryGetProperty("machineName", out var m) ? m.GetString() ?? "" : "",
                ExpiresAt: root.TryGetProperty("expiresAt", out var x) && x.TryGetDateTime(out var dt)
                    ? dt.ToUniversalTime() : DateTime.MinValue,
                Etag: etag);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Lock body failed to parse — treating as absent");
            return null;
        }
    }

    private async Task<(bool success, string? newEtag)> TryTakeOverExpiredLockAsync(string? currentEtag, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(currentEtag)) return (false, null);

        var newExpiry = DateTime.UtcNow.AddSeconds(_coopConfig.LeaseTtlSeconds);
        var body = BuildLockBody(newExpiry);
        var url = $"{_driveBaseUrl}/root:/{_lockPath}:/content";

        using var resp = await _graph.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("If-Match", currentEtag);
            return req;
        }, ct);

        if (resp.IsSuccessStatusCode)
            return (true, ParseEtagFromResponse(resp));

        return (false, null);
    }

    private string BuildLockBody(DateTime expiresAtUtc)
    {
        var now = DateTime.UtcNow;
        return JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            userId = _userId,
            userEmail = _userEmail,
            machineName = _machineName,
            claimedAt = now,
            expiresAt = expiresAtUtc,
            leaderVersion = _appVersion,
        });
    }

    private static string? ParseEtagFromResponse(HttpResponseMessage resp)
    {
        if (resp.Headers.ETag is not null) return resp.Headers.ETag.Tag;
        return null;
    }

    private readonly record struct LockInfo(
        string UserId,
        string UserEmail,
        string MachineName,
        DateTime ExpiresAt,
        string? Etag);
}
