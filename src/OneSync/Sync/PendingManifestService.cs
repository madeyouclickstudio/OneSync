using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// One entry per file that has unfinished work on a particular machine.
/// </summary>
internal sealed class PendingManifestEntry
{
    [JsonPropertyName("machine")]
    public string Machine { get; set; } = string.Empty;

    [JsonPropertyName("driveLetter")]
    public string DriveLetter { get; set; } = string.Empty;

    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("queuedAt")]
    public DateTime QueuedAtUtc { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}

internal sealed class PendingManifest
{
    [JsonPropertyName("entries")]
    public List<PendingManifestEntry> Entries { get; set; } = new();
}

/// <summary>
/// Reads and writes a small manifest at the root of the user's OneDrive listing
/// every pending upload across every machine the user signs into. Each OneSync instance:
///   - Snapshots its own pending uploads to the manifest on startup
///   - Adds an entry when an upload is queued
///   - Removes its entry when the upload completes
///   - At startup, surfaces a balloon if other machines have entries
///
/// Uses ETag/If-Match for safe concurrent updates from multiple machines.
/// </summary>
internal sealed class PendingManifestService
{
    private const string ManifestPath = "/OneSync/pending.json";
    private const string MetadataUrl = "https://graph.microsoft.com/v1.0/me/drive/root:" + ManifestPath;
    private const string ContentUrl = "https://graph.microsoft.com/v1.0/me/drive/root:" + ManifestPath + ":/content";

    private readonly GraphHttpClient _graph;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _lastETag;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public PendingManifestService(GraphHttpClient graph, ILogger logger)
    {
        _graph = graph;
        _logger = logger.ForContext("Component", "PendingManifest");
    }

    /// <summary>Read the current manifest from OneDrive. Returns null if the file
    /// doesn't exist yet (first time anyone has used the feature for this user).</summary>
    public async Task<PendingManifest?> ReadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var resp = await _graph.SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, ContentUrl), ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Debug("Manifest read returned {Status}", (int)resp.StatusCode);
                return null;
            }

            _lastETag = resp.Headers.ETag?.Tag;
            var json = await resp.Content.ReadAsStringAsync(ct);
            try
            {
                return JsonSerializer.Deserialize<PendingManifest>(json, _json) ?? new PendingManifest();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Manifest JSON malformed; treating as empty");
                return new PendingManifest();
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>Update this machine's entries in the manifest. Removes any
    /// entries with the same machine name first, then adds the new ones, then
    /// writes back. Uses optimistic concurrency via If-Match.</summary>
    public async Task UpdateThisMachineAsync(
        string machineName,
        IEnumerable<PendingManifestEntry> currentEntries,
        CancellationToken ct = default)
    {
        var newEntries = new List<PendingManifestEntry>(currentEntries);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var manifest = await ReadAsync(ct) ?? new PendingManifest();

                // Strip out any prior entries for this machine - they're being replaced
                manifest.Entries.RemoveAll(e =>
                    string.Equals(e.Machine, machineName, StringComparison.OrdinalIgnoreCase));
                manifest.Entries.AddRange(newEntries);

                // Drop stale entries older than 30 days (from any machine)
                var cutoff = DateTime.UtcNow.AddDays(-30);
                manifest.Entries.RemoveAll(e => e.QueuedAtUtc < cutoff);

                var json = JsonSerializer.Serialize(manifest, _json);

                // Capture for the factory; need a fresh StringContent per attempt
                // because HttpContent is also single-use.
                var bodyJson = json;
                var ifMatch = _lastETag;
                using var resp = await _graph.SendAsync(() =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Put, ContentUrl)
                    {
                        Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrEmpty(ifMatch))
                        r.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                    return r;
                }, ct);
                if (resp.IsSuccessStatusCode)
                {
                    _lastETag = resp.Headers.ETag?.Tag;
                    _logger.Information(
                        "Pending manifest updated: this machine has {Count} entries (total in manifest: {Total})",
                        newEntries.Count, manifest.Entries.Count);
                    return;
                }

                if (resp.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    _logger.Debug("Manifest etag conflict on attempt {Attempt} - re-reading", attempt + 1);
                    _lastETag = null; // force re-read next loop
                    await Task.Delay(TimeSpan.FromMilliseconds(200 + attempt * 300), ct);
                    continue;
                }

                _logger.Warning("Manifest write returned {Status} - giving up", (int)resp.StatusCode);
                return;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Manifest update attempt {Attempt} failed", attempt + 1);
                if (attempt == 4) return;
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct);
            }
        }
    }
}
