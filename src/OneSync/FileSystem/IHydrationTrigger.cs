using OneSync.Config;

namespace OneSync.FileSystem;

/// <summary>
/// Decouples the Dokan filesystem from the sync layer so OneSyncDokanFS doesn't
/// need a direct reference to Sync.HydrationService.
/// </summary>
internal interface IHydrationTrigger
{
    bool HydrateIfNeeded(DriveConfig drive, string relativePath, string localPath);

    /// <summary>Returns the remote size of a placeholder, or 0 if unknown.</summary>
    long GetRemoteSize(DriveConfig drive, string relativePath);

    /// <summary>
    /// If the folder at <paramref name="folderRelativePath"/> has no local children,
    /// fetches its immediate children from Graph and creates placeholders.
    /// Returns true if new items were created.
    /// </summary>
    bool EnumerateFolderIfEmpty(DriveConfig drive, string folderRelativePath);

    /// <summary>True if Graph is currently in a throttled cooldown. Dokan
    /// callbacks should check this before invoking HydrateIfNeeded so they
    /// don't block on the cooldown wait for up to 10 minutes.</summary>
    bool IsGraphInCooldown { get; }

    /// <summary>Records that a file was just opened/accessed locally. Updates
    /// LastAccessedAt in MetadataStore so the LRU eviction service knows the
    /// file is in active use. Cheap — single LiteDB update.</summary>
    void NotifyAccessed(DriveConfig drive, string relativePath);
}
