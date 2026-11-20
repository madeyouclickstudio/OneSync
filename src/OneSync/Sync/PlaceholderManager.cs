using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OneSync.Config;
using OneSync.Shell;
using OneSync.Util;
using Serilog;

namespace OneSync.Sync;

/// <summary>
/// Keeps the local backing folder in step with the cloud: creates placeholders
/// (empty sparse files) for cloud files, folders for cloud folders, and applies
/// cloud changes and deletions. A local file with changes that haven't been
/// uploaded is never truncated or deleted here.
/// </summary>
internal sealed class PlaceholderManager
{
    private static readonly TimeSpan OwnWriteWindow = TimeSpan.FromSeconds(10);

    private readonly MetadataStore _metadata;
    private readonly LocalChangeSuppressor _suppressor;
    private readonly ILogger _logger;
    private readonly ThumbnailPrefetcher? _thumbnails;
    private readonly object _applyLock = new();

    public PlaceholderManager(
        MetadataStore metadata,
        LocalChangeSuppressor suppressor,
        ILogger logger,
        ThumbnailPrefetcher? thumbnails = null)
    {
        _metadata = metadata;
        _suppressor = suppressor;
        _logger = logger;
        _thumbnails = thumbnails;
    }

    public static string LocalPathFor(DriveConfig drive, string relativePath)
    {
        var winRel = PathUtil.ToWindowsRelative(relativePath);
        return string.IsNullOrEmpty(winRel) ? drive.LocalRootPath : Path.Combine(drive.LocalRootPath, winRel);
    }

    public enum ApplyResult { None, Created, Updated }

    /// <summary>True for folders whose contents we keep up to date: the drive root
    /// once it has been listed, and any folder we hold a record for.</summary>
    public bool IsTrackedFolder(DriveConfig drive, string relativePath)
    {
        if (relativePath == "/")
            return _metadata.GetEnumerationState(drive.ConfigId, "/")?.LastStatusCode == 200;
        return _metadata.Get(drive.ConfigId, relativePath) is { IsFolder: true };
    }

    /// <summary>
    /// Merges what the cloud says about an item with what we already know, saves
    /// it, and brings the local copy in line. Used for delta changes, folder
    /// listings and the shared delta cache alike.
    /// </summary>
    public ApplyResult ApplyRemote(DriveConfig drive, RemoteItem incoming)
    {
        lock (_applyLock)
        {
            FollowRemoteMove(drive, incoming);

            var existing = _metadata.Get(drive.ConfigId, incoming.RelativePath);
            var localPath = LocalPathFor(drive, incoming.RelativePath);
            var merged = Merge(existing, incoming, localPath);
            _metadata.Upsert(merged);
            CreateOrUpdate(drive, merged);
            return existing is null ? ApplyResult.Created : ApplyResult.Updated;
        }
    }

    private RemoteItem Merge(RemoteItem? existing, RemoteItem incoming, string localPath)
    {
        incoming.PlaceholderCreated = existing?.PlaceholderCreated ?? false;
        incoming.LastAccessedAt = existing?.LastAccessedAt ?? default;
        if (incoming.IsFolder) return incoming;

        var fi = new FileInfo(localPath);
        bool hasContent = fi.Exists && !LocalState.IsPlaceholder(fi) &&
                          (fi.Length > 0 || (existing is { IsFolder: false } && LocalState.HasBaseline(existing)));

        if (existing is null || existing.IsFolder)
        {
            if (hasContent)
            {
                // A local copy we hold no record for (a file made here with the same
                // name, or one whose record was lost). Keep it; the reconcile pass
                // works out whether it matches the cloud or needs a conflict copy.
                incoming.Hydrated = true;
                incoming.RemoteChangedSinceBaseline = true;
            }
            return incoming;
        }

        bool contentChanged = ContentDiffers(existing, incoming);
        if (!contentChanged)
        {
            incoming.Hydrated = existing.Hydrated;
            incoming.LocalLength = existing.LocalLength;
            incoming.LocalWriteTicks = existing.LocalWriteTicks;
            incoming.RemoteChangedSinceBaseline = existing.RemoteChangedSinceBaseline;
            if (string.IsNullOrEmpty(incoming.ContentHash)) incoming.ContentHash = existing.ContentHash;
            return incoming;
        }

        if (hasContent && (LocalState.IsDirty(fi, existing) || existing.RemoteChangedSinceBaseline))
        {
            // Both sides changed. Keep the local edits and the eTag they were based
            // on, so the upload fails its If-Match check and becomes a conflict copy
            // instead of silently replacing the newer cloud version.
            incoming.ETag = existing.ETag;
            incoming.CTag = existing.CTag;
            incoming.ContentHash = existing.ContentHash;
            incoming.Hydrated = true;
            incoming.LocalLength = existing.LocalLength;
            incoming.LocalWriteTicks = existing.LocalWriteTicks;
            incoming.RemoteChangedSinceBaseline = true;
            _logger.Information("Cloud copy of {Path} changed while it has local edits; keeping both",
                incoming.RelativePath);
            return incoming;
        }

        // Cloud has newer content and the local copy is clean: back to a placeholder,
        // downloaded again on next open.
        incoming.Hydrated = false;
        LocalState.ClearBaseline(incoming);
        return incoming;
    }

    private static bool ContentDiffers(RemoteItem existing, RemoteItem incoming)
    {
        if (!string.IsNullOrEmpty(existing.ContentHash) && !string.IsNullOrEmpty(incoming.ContentHash))
            return !string.Equals(existing.ContentHash, incoming.ContentHash, StringComparison.Ordinal)
                   || existing.Size != incoming.Size;
        if (!string.IsNullOrEmpty(existing.CTag) && !string.IsNullOrEmpty(incoming.CTag))
            return !string.Equals(existing.CTag, incoming.CTag, StringComparison.Ordinal);
        return existing.ETag != incoming.ETag || existing.Size != incoming.Size;
    }

    /// <summary>If the cloud moved or renamed an item we already have, move the
    /// local copy with it rather than leaving a stale duplicate behind.</summary>
    private void FollowRemoteMove(DriveConfig drive, RemoteItem incoming)
    {
        if (string.IsNullOrEmpty(incoming.RemoteItemId)) return;
        var previous = _metadata.GetById(incoming.RemoteItemId);
        if (previous is null || previous.DriveConfigId != drive.ConfigId) return;
        if (previous.RelativePath.Equals(incoming.RelativePath, StringComparison.Ordinal)) return;

        var oldLocal = LocalPathFor(drive, previous.RelativePath);
        var newLocal = LocalPathFor(drive, incoming.RelativePath);
        try
        {
            bool oldExists = previous.IsFolder ? Directory.Exists(oldLocal) : File.Exists(oldLocal);
            bool newExists = File.Exists(newLocal) || Directory.Exists(newLocal);
            if (oldExists && !newExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newLocal)!);
                _suppressor.SuppressFor(oldLocal, OwnWriteWindow);
                _suppressor.SuppressFor(newLocal, OwnWriteWindow);
                if (previous.IsFolder) Directory.Move(oldLocal, newLocal);
                else File.Move(oldLocal, newLocal);
                ShellNotifier.NotifyRenamed(drive, previous.RelativePath, incoming.RelativePath, previous.IsFolder);
            }
            _metadata.MovePath(drive.ConfigId, previous.RelativePath, incoming.RelativePath);
            _logger.Information("Followed cloud move {Old} -> {New}", previous.RelativePath, incoming.RelativePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not follow cloud move {Old} -> {New}", previous.RelativePath, incoming.RelativePath);
        }
    }

    /// <summary>
    /// Makes the local copy match a saved record: a folder, a placeholder, or
    /// (for a downloaded file) nothing at all.
    /// </summary>
    public void CreateOrUpdate(DriveConfig drive, RemoteItem item)
    {
        var fullPath = LocalPathFor(drive, item.RelativePath);

        try
        {
            if (item.IsFolder)
            {
                bool dirWasNew = !Directory.Exists(fullPath);
                if (dirWasNew)
                {
                    _suppressor.SuppressFor(fullPath, OwnWriteWindow);
                    Directory.CreateDirectory(fullPath);
                }
                ApplyFolderTimes(fullPath, item);
                _metadata.MarkPlaceholderCreated(drive.ConfigId, item.RelativePath);
                if (dirWasNew)
                    ShellNotifier.NotifyCreated(drive, item.RelativePath, isFolder: true);
                return;
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                _suppressor.SuppressFor(dir, OwnWriteWindow);
                Directory.CreateDirectory(dir);
            }

            var existing = new FileInfo(fullPath);
            bool fileWasNew = !existing.Exists;

            if (existing.Exists)
            {
                if (LocalState.IsPlaceholder(existing))
                {
                    ApplyFileTimes(fullPath, item, existing);
                    _metadata.MarkPlaceholderCreated(drive.ConfigId, item.RelativePath);
                    return;
                }

                // Real content on disk. Leave it alone if it's the current version
                // or holds edits of its own.
                if (item.Hydrated || item.RemoteChangedSinceBaseline || LocalState.IsDirty(existing, item))
                {
                    _metadata.MarkPlaceholderCreated(drive.ConfigId, item.RelativePath);
                    return;
                }
            }

            _suppressor.SuppressFor(fullPath, OwnWriteWindow);
            using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                SetSparse(fs.SafeFileHandle, true);
            }
            ApplyFileTimes(fullPath, item, null);

            if (item.Hydrated || LocalState.HasBaseline(item))
                _metadata.MarkDehydrated(drive.ConfigId, item.RelativePath);
            _metadata.MarkPlaceholderCreated(drive.ConfigId, item.RelativePath);
            SyncStateMarker.Mark(fullPath, SyncOverlayState.CloudOnly);

            if (_thumbnails != null && ThumbnailPrefetcher.IsThumbnailableExtension(fullPath))
                _thumbnails.EnqueueIfMissing(drive, item, fullPath);

            if (fileWasNew)
                ShellNotifier.NotifyCreated(drive, item.RelativePath);
            else
                ShellNotifier.NotifyUpdated(drive, item.RelativePath);

            _logger.Debug("Placeholder created: {Drive}:{Path} (remote size {Size})",
                drive.Letter, item.RelativePath, item.Size);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "CreatePlaceholder failed for {Path}", fullPath);
        }
    }

    private static void ApplyFileTimes(string path, RemoteItem item, FileInfo? current)
    {
        try
        {
            if (item.LastModifiedDateTime != default &&
                (current is null || current.LastWriteTimeUtc != item.LastModifiedDateTime))
                File.SetLastWriteTimeUtc(path, item.LastModifiedDateTime);
            if (item.CreatedDateTime != default &&
                (current is null || current.CreationTimeUtc != item.CreatedDateTime))
                File.SetCreationTimeUtc(path, item.CreatedDateTime);
        }
        catch { }
    }

    private static void ApplyFolderTimes(string path, RemoteItem item)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if (item.LastModifiedDateTime != default && di.LastWriteTimeUtc != item.LastModifiedDateTime)
                Directory.SetLastWriteTimeUtc(path, item.LastModifiedDateTime);
            if (item.CreatedDateTime != default && di.CreationTimeUtc != item.CreatedDateTime)
                Directory.SetCreationTimeUtc(path, item.CreatedDateTime);
        }
        catch { }
    }

    /// <summary>
    /// Applies a cloud deletion locally. Placeholders and unchanged downloads go;
    /// anything with local edits stays and loses its record, so it is uploaded
    /// again as a new file rather than lost. Returns the paths that were kept.
    /// </summary>
    public List<string> RemovePlaceholder(DriveConfig drive, string relativePath, bool isFolder)
    {
        var kept = new List<string>();
        var fullPath = LocalPathFor(drive, relativePath);
        bool didRemove = false;
        try
        {
            _suppressor.Suppress(fullPath);
            try
            {
                if (isFolder && Directory.Exists(fullPath))
                    didRemove = RemoveTree(drive, fullPath, kept);
                else if (!isFolder && File.Exists(fullPath))
                    didRemove = RemoveFile(drive, fullPath, kept);
            }
            finally
            {
                _suppressor.Release(fullPath);
            }

            if (isFolder) _metadata.DeleteTree(drive.ConfigId, relativePath);
            else _metadata.Delete(drive.ConfigId, relativePath);

            if (kept.Count > 0)
                _logger.Warning("Cloud deleted {Path} but {Count} local file(s) under it had unsynced changes; kept them",
                    relativePath, kept.Count);
            else
                _logger.Debug("Removed placeholder: {Drive}:{Path}", drive.Letter, relativePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "RemovePlaceholder failed for {Path}", fullPath);
        }

        if (didRemove)
            ShellNotifier.NotifyDeleted(drive, relativePath, isFolder);
        return kept;
    }

    private bool RemoveFile(DriveConfig drive, string fullPath, List<string> kept)
    {
        var fi = new FileInfo(fullPath);
        var rel = RelativeFor(drive, fullPath);
        var meta = _metadata.Get(drive.ConfigId, rel);
        if (LocalState.IsDirty(fi, meta) || meta?.RemoteChangedSinceBaseline == true)
        {
            kept.Add(rel);
            return false;
        }
        fi.Attributes &= ~FileAttributes.ReadOnly;
        fi.Delete();
        return true;
    }

    private bool RemoveTree(DriveConfig drive, string dirPath, List<string> kept)
    {
        foreach (var file in Directory.EnumerateFiles(dirPath))
        {
            try { RemoveFile(drive, file, kept); }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not remove {Path}", file);
                kept.Add(RelativeFor(drive, file));
            }
        }
        foreach (var sub in Directory.EnumerateDirectories(dirPath))
            RemoveTree(drive, sub, kept);

        if (Directory.EnumerateFileSystemEntries(dirPath).Any()) return false;
        Directory.Delete(dirPath);
        return true;
    }

    private static string RelativeFor(DriveConfig drive, string fullPath) =>
        PathUtil.NormalizeRelative(Path.GetRelativePath(drive.LocalRootPath, fullPath));

    /// <summary>
    /// Sets or clears the sparse flag. OneSync uses it to tell its own files
    /// (placeholders and downloads in progress) from real content: nothing
    /// written through the drive is ever sparse.
    /// </summary>
    internal static void SetSparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle, bool sparse)
    {
        try
        {
            byte flag = sparse ? (byte)1 : (byte)0;
            DeviceIoControl(handle, FSCTL_SET_SPARSE, ref flag, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { }
    }

    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint dwIoControlCode,
        ref byte lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
