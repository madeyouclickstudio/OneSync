using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Config;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync.Cooperative;

/// <summary>
/// The leader's view of the library, serialised to <c>{controlFolder}/delta-cache.json</c>.
/// Readers pull this file to populate their local placeholders without each running
/// their own delta query against the library.
///
/// Schema is forward-compatible: <see cref="SchemaVersion"/> is bumped on breaking
/// changes; readers should compare and fall back to per-user polling on mismatch.
/// </summary>
internal sealed record DeltaCachePayload
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonPropertyName("writtenAt")] public DateTime WrittenAt { get; init; }
    [JsonPropertyName("leaderUserId")] public string LeaderUserId { get; init; } = "";
    [JsonPropertyName("leaderEmail")] public string LeaderEmail { get; init; } = "";
    [JsonPropertyName("leaderMachine")] public string LeaderMachine { get; init; } = "";
    [JsonPropertyName("leaderVersion")] public string LeaderVersion { get; init; } = "";
    [JsonPropertyName("deltaToken")] public string? DeltaToken { get; init; }
    [JsonPropertyName("items")] public List<DeltaCacheItem> Items { get; init; } = new();
}

internal sealed record DeltaCacheItem
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("parentId")] public string ParentId { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "/";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("isFolder")] public bool IsFolder { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("etag")] public string? ETag { get; init; }
    [JsonPropertyName("webUrl")] public string? WebUrl { get; init; }
    [JsonPropertyName("lastModified")] public DateTime LastModified { get; init; }
}

/// <summary>
/// Read/write the cooperative-polling delta cache file inside a library.
/// Atomic updates via Graph ETag conditional headers.
/// </summary>
internal sealed class DeltaCache
{
    private readonly GraphHttpClient _graph;
    private readonly DriveConfig _drive;
    private readonly CooperativePollingConfig _coopConfig;
    private readonly ILogger _logger;
    private readonly string _driveBaseUrl;
    private readonly string _cachePath;

    private string? _lastWrittenEtag;
    private string? _lastWrittenContentHash;
    private string? _lastReadEtag;
    private int _writeAttemptsSinceLastForceWrite;

    public DeltaCache(GraphHttpClient graph, DriveConfig drive, CooperativePollingConfig coopConfig, ILogger logger)
    {
        _graph = graph;
        _drive = drive;
        _coopConfig = coopConfig;
        if (string.IsNullOrEmpty(drive.ResolvedDriveId))
            throw new InvalidOperationException("Drive must be resolved before DeltaCache can run");
        _driveBaseUrl = $"https://graph.microsoft.com/v1.0/drives/{drive.ResolvedDriveId}";
        var folder = string.IsNullOrWhiteSpace(coopConfig.ControlFolder) ? ".onesync" : coopConfig.ControlFolder.Trim('/');
        _cachePath = $"{folder}/delta-cache.json";
        _logger = logger
            .ForContext("Component", "DeltaCache")
            .ForContext("Drive", $"{drive.Letter}:");
    }

    /// <summary>
    /// Writes the cache if the content has changed since the last write, or if
    /// we haven't forced a refresh in a while (so a long-running leader still
    /// updates the WrittenAt timestamp readers can sanity-check).
    /// Returns true if a write happened, false if skipped.
    /// </summary>
    public async Task<bool> WriteIfChangedAsync(
        DeltaCachePayload payload, int forceWriteEveryNCycles, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        var contentHash = HashItemsOnly(payload);

        _writeAttemptsSinceLastForceWrite++;
        bool forceWrite = _writeAttemptsSinceLastForceWrite >= Math.Max(1, forceWriteEveryNCycles);

        if (!forceWrite && contentHash == _lastWrittenContentHash)
        {
            _logger.Debug("Delta cache unchanged since last write ({Items} items) — skipped",
                payload.Items.Count);
            return false;
        }

        var url = $"{_driveBaseUrl}/root:/{_cachePath}:/content";
        using var resp = await _graph.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new ByteArrayContent(bytes),
            };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (!string.IsNullOrEmpty(_lastWrittenEtag))
                req.Headers.TryAddWithoutValidation("If-Match", _lastWrittenEtag);
            return req;
        }, ct);

        if (resp.IsSuccessStatusCode)
        {
            _lastWrittenEtag = resp.Headers.ETag?.Tag;
            _lastWrittenContentHash = contentHash;
            _writeAttemptsSinceLastForceWrite = 0;
            int folderCount = 0, fileCount = 0;
            foreach (var i in payload.Items) { if (i.IsFolder) folderCount++; else fileCount++; }
            _logger.Information(
                "Wrote delta cache: {Folders} folders + {Files} files = {Total} items, {Size:N0} bytes ({Reason})",
                folderCount, fileCount, payload.Items.Count, bytes.Length,
                forceWrite ? "periodic refresh" : "content changed");
            return true;
        }

        if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            _logger.Warning(
                "Delta cache write rejected (412) — fetching current etag and retrying once");
            _lastWrittenEtag = null;
            _lastWrittenContentHash = null;

            // Recovery: fetch the current cache file to capture its server etag,
            // then retry the PUT with If-Match. This prevents the next write
            // from going out unconditionally and clobbering a peer's content.
            // If recovery also fails we give up this cycle — the next lock
            // renewal will surface whether we've genuinely lost leadership.
            var fresh = await FetchCurrentEtagAsync(ct);
            if (string.IsNullOrEmpty(fresh))
            {
                _logger.Warning(
                    "Delta cache 412 recovery: could not read current etag — skipping write this cycle");
                return false;
            }

            using var retry = await _graph.SendAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Put, url)
                {
                    Content = new ByteArrayContent(bytes),
                };
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                req.Headers.TryAddWithoutValidation("If-Match", fresh);
                return req;
            }, ct);

            if (retry.IsSuccessStatusCode)
            {
                _lastWrittenEtag = retry.Headers.ETag?.Tag;
                _lastWrittenContentHash = contentHash;
                _writeAttemptsSinceLastForceWrite = 0;
                _logger.Information(
                    "Delta cache write recovered after 412 (retry with fresh etag succeeded)");
                return true;
            }

            _logger.Warning(
                "Delta cache 412 recovery retry returned {Status} — skipping this cycle, lock renewal will reassess leadership",
                (int)retry.StatusCode);
            return false;
        }

        _logger.Warning("Delta cache write returned {Status}", (int)resp.StatusCode);
        return false;
    }

    /// <summary>
    /// HEAD/GET the cache file to capture its current server-side etag.
    /// Used by the 412 recovery path so the retry sends a valid If-Match
    /// header instead of going out unconditionally.
    /// </summary>
    private async Task<string?> FetchCurrentEtagAsync(CancellationToken ct)
    {
        var metaUrl = $"{_driveBaseUrl}/root:/{_cachePath}";
        using var resp = await _graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, metaUrl), ct);
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            // Drive items expose both eTag (any change) and cTag (content change).
            // Prefer eTag because it's what If-Match compares against for content PUTs.
            if (doc.RootElement.TryGetProperty("eTag", out var etagProp))
                return etagProp.GetString();
            return resp.Headers.ETag?.Tag;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "FetchCurrentEtagAsync parse failed");
            return null;
        }
    }

    /// <summary>
    /// Reads the cache from the library. Uses <c>If-None-Match</c> so that
    /// repeated reads of an unchanged cache return 304 — the reader pays
    /// only the round trip, not the body. Returns null if no content has
    /// changed since the last successful read, or if the cache is absent.
    /// </summary>
    public async Task<DeltaCachePayload?> ReadAsync(CancellationToken ct)
    {
        var url = $"{_driveBaseUrl}/root:/{_cachePath}:/content";
        using var resp = await _graph.SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_lastReadEtag))
                req.Headers.TryAddWithoutValidation("If-None-Match", _lastReadEtag);
            return req;
        }, ct);

        if (resp.StatusCode == HttpStatusCode.NotModified)
        {
            _logger.Debug("Delta cache unchanged since last read (304)");
            return null;
        }
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Warning("Delta cache read returned {Status}", (int)resp.StatusCode);
            return null;
        }

        try
        {
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<DeltaCachePayload>(stream, cancellationToken: ct);
            if (payload is null) return null;
            if (payload.SchemaVersion != DeltaCachePayload.CurrentSchemaVersion)
            {
                _logger.Warning(
                    "Delta cache schema mismatch (got {Got}, expected {Expected}) — ignoring",
                    payload.SchemaVersion, DeltaCachePayload.CurrentSchemaVersion);
                return null;
            }
            _lastReadEtag = resp.Headers.ETag?.Tag;
            return payload;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Delta cache parse failed");
            return null;
        }
    }

    /// <summary>
    /// Stable hash of just the items list — independent of leader identity,
    /// WrittenAt timestamp, and delta token. Used to detect whether anything
    /// users care about has actually changed since the last write.
    /// </summary>
    private static string HashItemsOnly(DeltaCachePayload payload)
    {
        using var sha = SHA256.Create();
        using var ms = new System.IO.MemoryStream();
        foreach (var item in payload.Items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            // ETag covers content + name + parent; size is a belt-and-braces
            // catch for items the server doesn't always re-stamp.
            var line = $"{item.Id}|{item.ETag}|{item.Size}|{item.Path}\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(ms));
    }

    /// <summary>
    /// Build a payload from the leader's local metadata snapshot. When
    /// <paramref name="maxAgeDays"/> &gt; 0, applies the sliding-window filter:
    /// every folder is kept (so the directory structure stays browsable on
    /// the reader side) but files are only included if they've been modified
    /// within the window. Older files populate on demand via Dokan's lazy
    /// fallback when a reader navigates to their parent folder.
    /// </summary>
    public static DeltaCachePayload BuildPayload(
        string leaderUserId, string leaderEmail, string leaderMachine, string leaderVersion,
        string? deltaToken, IEnumerable<RemoteItem> items,
        int maxAgeDays = 0)
    {
        var materialised = items.Where(it => !string.IsNullOrEmpty(it.RemoteItemId)).ToList();
        IEnumerable<RemoteItem> selected = materialised;
        if (maxAgeDays > 0)
        {
            var cutoffUtc = DateTime.UtcNow.AddDays(-maxAgeDays);
            selected = materialised.Where(it =>
                it.IsFolder || it.LastModifiedDateTime.ToUniversalTime() > cutoffUtc);
        }

        var cacheItems = selected
            .Select(it => new DeltaCacheItem
            {
                Id = it.RemoteItemId,
                ParentId = it.ParentRemoteItemId,
                Path = it.RelativePath,
                Name = it.Name,
                IsFolder = it.IsFolder,
                Size = it.Size,
                ETag = it.ETag,
                WebUrl = it.WebUrl,
                LastModified = it.LastModifiedDateTime,
            })
            .ToList();

        return new DeltaCachePayload
        {
            SchemaVersion = DeltaCachePayload.CurrentSchemaVersion,
            WrittenAt = DateTime.UtcNow,
            LeaderUserId = leaderUserId,
            LeaderEmail = leaderEmail,
            LeaderMachine = leaderMachine,
            LeaderVersion = leaderVersion,
            DeltaToken = deltaToken,
            Items = cacheItems,
        };
    }
}
