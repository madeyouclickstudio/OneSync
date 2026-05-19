using System;
using LiteDB;

namespace OneSync.Sync;

/// <summary>
/// Local mirror of remote (Graph) file metadata. Used to render placeholders
/// in Explorer and trigger hydration on read.
/// </summary>
internal sealed class RemoteItem
{
    [BsonId]
    public string Key { get; set; } = string.Empty; // driveConfigId + ":" + remotePath

    public string DriveConfigId { get; set; } = string.Empty;
    public string RemoteItemId { get; set; } = string.Empty;
    public string ParentRemoteItemId { get; set; } = string.Empty;

    /// <summary>POSIX-style relative path from drive root, leading slash.</summary>
    public string RelativePath { get; set; } = "/";

    public string Name { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public long Size { get; set; }

    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public string? ETag { get; set; }
    public string? ContentHash { get; set; } // sha256/quickXor

    /// <summary>SharePoint / OneDrive web URL for this item. Used by OfficeLauncher
    /// to build `ms-word:ofe|u|<url>` redirects so Office opens the cloud copy
    /// directly (enables co-authoring and AutoSave). Back-filled by DeltaPoller
    /// from Graph's `webUrl` property.</summary>
    public string? WebUrl { get; set; }

    /// <summary>True if the local placeholder/sparse file has been replaced with full content.</summary>
    public bool Hydrated { get; set; }

    /// <summary>True if we've created the local sparse file/directory.</summary>
    public bool PlaceholderCreated { get; set; }

    public DateTime LastSyncedAt { get; set; }

    /// <summary>When this file was last opened locally (Dokan CreateFile) or hydrated.
    /// Used by LruEvictionService to pick eviction victims — oldest accessed first.
    /// Updated by HydrationService on download and by the Dokan layer on file open.</summary>
    public DateTime LastAccessedAt { get; set; }

    public static string MakeKey(string driveConfigId, string relativePath) =>
        $"{driveConfigId}::{relativePath.ToLowerInvariant()}";
}
