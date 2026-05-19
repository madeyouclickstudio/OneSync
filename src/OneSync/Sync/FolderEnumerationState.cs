using System;
using LiteDB;

namespace OneSync.Sync;

/// <summary>
/// Persistent record of what happened the last time a folder was enumerated
/// against Graph. Used to suppress repeat on-demand enumeration calls across
/// process restarts (the in-memory ConcurrentDictionary in HydrationService
/// is cleared on every restart, which used to cause storms of redundant Graph
/// calls after a crash).
///
/// Stored in MetadataStore (LiteDB) keyed by {DriveConfigId, RelativePath}.
/// </summary>
internal sealed class FolderEnumerationState
{
    /// <summary>Composite key: "{DriveConfigId}::{relativePathLowercased}".
    /// Lowercased so case-different lookups on Windows paths still hit the
    /// same record (matches the RemoteItem convention).</summary>
    [BsonId]
    public string Key { get; set; } = string.Empty;

    public string DriveConfigId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The Graph response status the last time we enumerated.
    /// 200 = exists with items; 404 = remote folder missing (negative-cache);
    /// any other = transient/error (don't trust, will re-try).</summary>
    public int LastStatusCode { get; set; }

    public DateTime LastAttemptUtc { get; set; }

    /// <summary>How long to honour a "this folder doesn't exist" 404. Subsequent
    /// browses within this window skip the Graph call entirely.</summary>
    public DateTime NegativeCacheExpiresUtc { get; set; }

    public static string MakeKey(string driveConfigId, string relativePath) =>
        $"{driveConfigId}::{relativePath.ToLowerInvariant()}";
}
