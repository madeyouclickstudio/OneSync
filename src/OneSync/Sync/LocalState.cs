using System;
using System.IO;

namespace OneSync.Sync;

/// <summary>
/// Compares a local file with what we last knew the cloud held, so nothing that
/// hasn't been uploaded is ever overwritten, truncated or deleted.
/// </summary>
internal static class LocalState
{
    /// <summary>An empty sparse file: the placeholder we create for a cloud-only file.</summary>
    public static bool IsPlaceholder(FileInfo fi) =>
        fi.Exists && fi.Length == 0 && (fi.Attributes & FileAttributes.SparseFile) != 0;

    /// <summary>
    /// A placeholder part-way through downloading, or one left like that by a
    /// crash. Downloads keep the file sparse until they finish, and only then
    /// record it as downloaded.
    /// </summary>
    public static bool IsInterruptedDownload(FileInfo fi, RemoteItem? meta) =>
        fi.Exists && fi.Length > 0 && (fi.Attributes & FileAttributes.SparseFile) != 0 &&
        meta is { IsFolder: false, Hydrated: false } && !HasBaseline(meta);

    /// <summary>
    /// True when the local file holds content the cloud might not have: a file we
    /// have no record of, or one that has changed since it last matched the cloud.
    /// </summary>
    public static bool IsDirty(FileInfo fi, RemoteItem? meta)
    {
        fi.Refresh();
        if (!fi.Exists) return false;
        if (IsPlaceholder(fi) || IsInterruptedDownload(fi, meta)) return false;
        if (meta is null || meta.IsFolder) return true;

        if (HasBaseline(meta))
            return fi.Length != meta.LocalLength || fi.LastWriteTimeUtc.Ticks != meta.LocalWriteTicks;

        // Records from before the baseline existed: all we can go on is the size.
        // The reconcile pass settles same-size cases with a content hash.
        return !(meta.Hydrated && fi.Length == meta.Size);
    }

    public static bool HasBaseline(RemoteItem meta) => meta.LocalWriteTicks != 0;

    public static void SetBaseline(RemoteItem meta, FileInfo fi)
    {
        fi.Refresh();
        meta.LocalLength = fi.Exists ? fi.Length : 0;
        meta.LocalWriteTicks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0;
    }

    public static void ClearBaseline(RemoteItem meta)
    {
        meta.LocalLength = 0;
        meta.LocalWriteTicks = 0;
    }
}
