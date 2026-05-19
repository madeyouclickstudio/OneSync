using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OneSync.Config;
using OneSync.Shell;
using OneSync.Util;
using Serilog;
// ShellNotifier lives in OneSync.Util; covered by the using above.

namespace OneSync.Sync;

/// <summary>
/// Creates and maintains local "placeholder" files that mirror remote metadata.
/// Placeholders are sparse files (0 bytes on disk) sized to the remote file's
/// length. The first read triggers hydration via <see cref="HydrationService"/>.
/// </summary>
internal sealed class PlaceholderManager
{
    private readonly MetadataStore _metadata;
    private readonly ILogger _logger;
    private readonly ThumbnailPrefetcher? _thumbnails;
    private readonly Dictionary<string, DriveConfig> _drivesById;

    public PlaceholderManager(
        MetadataStore metadata,
        ILogger logger,
        ThumbnailPrefetcher? thumbnails = null,
        IEnumerable<DriveConfig>? drives = null)
    {
        _metadata = metadata;
        _logger = logger;
        _thumbnails = thumbnails;
        _drivesById = new Dictionary<string, DriveConfig>(StringComparer.OrdinalIgnoreCase);
        if (drives != null)
            foreach (var d in drives) _drivesById[d.ConfigId] = d;
    }

    /// <summary>
    /// Creates an empty placeholder file on disk with the right metadata.
    /// The Dokan layer will trigger hydration when the file is opened/read.
    /// </summary>
    public void CreateOrUpdate(DriveConfig drive, RemoteItem item)
    {
        var winRel = PathUtil.ToWindowsRelative(item.RelativePath);
        var fullPath = string.IsNullOrEmpty(winRel)
            ? drive.LocalRootPath
            : Path.Combine(drive.LocalRootPath, winRel);

        try
        {
            if (item.IsFolder)
            {
                bool dirWasNew = !Directory.Exists(fullPath);
                Directory.CreateDirectory(fullPath);
                _metadata.MarkPlaceholderCreated(drive.ConfigId, item.RelativePath);
                if (dirWasNew)
                    ShellNotifier.NotifyCreated(drive, item.RelativePath, isFolder: true);
                // Existing folder, no SHChangeNotify — saves a pile of broadcasts
                // during RebuildMissingPlaceholders on a 6000-item drive.
                return;
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var existing = File.Exists(fullPath) ? new FileInfo(fullPath) : null;
            bool fileWasNew = existing == null;

            // If the local file already exists AND its size matches AND it's been hydrated,
            // don't touch it. (No SHChangeNotify — nothing has changed for Explorer to
            // re-paint.)
            if (existing != null && item.Hydrated && existing.Length == item.Size)
            {
                _metadata.MarkPlaceholderCreated(drive.ConfigId, item.RelativePath);
                return;
            }

            // If the local file already exists as a 0-byte placeholder AND the remote
            // hasn't changed (item not hydrated yet, no size change recorded), nothing
            // to notify Explorer about either.
            if (existing != null && !item.Hydrated && existing.Length == 0)
            {
                _metadata.MarkPlaceholderCreated(drive.ConfigId, item.RelativePath);
                return;
            }

            // Create empty file with sparse attribute. We DO NOT set the length to the remote
            // size, because Explorer would show 0 bytes anyway for a sparse file with no
            // valid data range. Instead we store the remote size in metadata; if the user
            // requests a read we hydrate. Explorer's "size" column reads the actual file
            // length (0 bytes).
            //
            // Trade-off: this is "metadata visibility" not "size visibility". Files appear
            // by name; size shows as 0 until hydrated. Acceptable for an MVP.
            using (var fs = File.Create(fullPath))
            {
                MarkSparse(fs.SafeFileHandle.DangerousGetHandle());
            }

            if (item.LastModifiedDateTime != default)
                File.SetLastWriteTime(fullPath, item.LastModifiedDateTime);
            if (item.CreatedDateTime != default)
                File.SetCreationTime(fullPath, item.CreatedDateTime);

            _metadata.MarkPlaceholderCreated(drive.ConfigId, item.RelativePath);
            SyncStateMarker.Mark(fullPath, SyncOverlayState.CloudOnly);

            // Best-effort thumbnail prefetch for image placeholders so Explorer
            // shows real previews instead of generic icons.
            if (_thumbnails != null && ThumbnailPrefetcher.IsThumbnailableExtension(fullPath))
                _thumbnails.EnqueueIfMissing(drive, item, fullPath);

            // Tell Explorer so the file appears in any open folder views
            // without the user pressing F5. CREATE for new placeholders,
            // UPDATEITEM for re-create of an existing one (changed size/etag).
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

    public void RemovePlaceholder(DriveConfig drive, string relativePath, bool isFolder)
    {
        var winRel = PathUtil.ToWindowsRelative(relativePath);
        var fullPath = Path.Combine(drive.LocalRootPath, winRel);
        bool didRemove = false;
        try
        {
            if (isFolder)
            {
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                    didRemove = true;
                }
            }
            else if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                didRemove = true;
            }
            _metadata.Delete(drive.ConfigId, relativePath);
            _logger.Debug("Removed placeholder: {Drive}:{Path}", drive.Letter, relativePath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "RemovePlaceholder failed for {Path}", fullPath);
        }

        // Tell Explorer so the entry disappears from any open folder views.
        if (didRemove)
            ShellNotifier.NotifyDeleted(drive, relativePath, isFolder);
    }

    private static void MarkSparse(IntPtr handle)
    {
        try
        {
            DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0,
                out _, IntPtr.Zero);
        }
        catch { /* best effort */ }
    }

    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
