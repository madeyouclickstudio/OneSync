using System;
using System.IO;
using System.Security.Cryptography;
using OneSync.Util;

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
    /// A placeholder, or an empty file standing in for one: older versions made
    /// placeholders without the sparse flag. Either way the content is only in
    /// the cloud.
    /// </summary>
    public static bool IsUnhydrated(FileInfo fi, RemoteItem? meta)
    {
        if (IsPlaceholder(fi)) return true;
        return fi.Exists && fi.Length == 0 && meta is { IsFolder: false, Hydrated: false } &&
               meta.Size > 0 && !HasBaseline(meta);
    }

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

        // No baseline: a record from before baselines were kept, or a copy we never
        // recorded downloading. Treat it as changed until an upload pass has hashed
        // it and set a baseline.
        return true;
    }

    /// <summary>
    /// Like <see cref="IsDirty"/>, but for a record with no baseline it hashes the
    /// file against the cloud copy rather than assuming it changed. For the rarer
    /// paths that would otherwise keep or re-upload a file needlessly.
    /// </summary>
    public static bool IsDirtyChecked(FileInfo fi, RemoteItem? meta)
    {
        if (!IsDirty(fi, meta)) return false;
        if (meta is null || meta.IsFolder || HasBaseline(meta)) return true;
        if (!meta.Hydrated || fi.Length != meta.Size) return true;
        try { return !MatchesHash(fi.FullName, meta.ContentHash); }
        catch { return true; }
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

    /// <summary>True for a quickXorHash, as opposed to the sha256Hash some drives report instead.</summary>
    public static bool IsQuickXorHash(string? hash) =>
        !string.IsNullOrEmpty(hash) && !IsSha256Hex(hash);

    /// <summary>
    /// True if the file's content matches a hash from the cloud, computed with
    /// whichever algorithm the cloud used. False when there's no hash to compare.
    /// </summary>
    public static bool MatchesHash(string path, string? cloudHash)
    {
        if (string.IsNullOrEmpty(cloudHash)) return false;
        if (IsSha256Hex(cloudHash))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            var hex = Convert.ToHexString(SHA256.HashData(fs));
            return string.Equals(hex, cloudHash, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(QuickXorHash.ComputeBase64(path), cloudHash, StringComparison.Ordinal);
    }

    private static bool IsSha256Hex(string hash)
    {
        if (hash.Length != 64) return false;
        foreach (var c in hash)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
