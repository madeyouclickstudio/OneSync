using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Threading.Tasks;
using DokanNet;
using OneSync.Config;
using Serilog;
using FileAccess = DokanNet.FileAccess;

namespace OneSync.FileSystem;

/// <summary>
/// Dokan filesystem pass-through over local NTFS. Custom overrides only on
/// GetDiskFreeSpace (cloud quota) and GetVolumeInformation (custom label).
/// </summary>
internal sealed class OneSyncDokanFS : IDokanOperations
{
    private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData
        | FileAccess.AppendData | FileAccess.Execute
        | FileAccess.GenericExecute | FileAccess.GenericWrite | FileAccess.GenericRead;

    private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData
        | FileAccess.Delete | FileAccess.GenericWrite;

    private readonly string _localRoot;
    private readonly DriveConfig _driveConfig;
    private readonly QuotaCache _quotaCache;
    private readonly ILogger _logger;
    private readonly Action<string, WatcherChangeTypes>? _onLocalChange;
    private readonly IHydrationTrigger? _hydration;

    public OneSyncDokanFS(
        DriveConfig driveConfig,
        QuotaCache quotaCache,
        ILogger logger,
        Action<string, WatcherChangeTypes>? onLocalChange = null,
        IHydrationTrigger? hydration = null)
    {
        _driveConfig = driveConfig;
        _localRoot = driveConfig.LocalRootPath;
        _quotaCache = quotaCache;
        _logger = logger.ForContext("Drive", driveConfig.Letter);
        _onLocalChange = onLocalChange;
        _hydration = hydration;
        Directory.CreateDirectory(_localRoot);
    }

    /// <summary>
    /// Name of the OneSync control folder (under each drive root). Holds the
    /// cooperative-polling lock + delta cache. Marked Hidden+System in Explorer
    /// and rejected by DeleteFile/DeleteDirectory/MoveFile so users can't delete
    /// or rename it through normal file operations.
    /// </summary>
    public static string ProtectedFolderName { get; set; } = ".onesync";

    private static bool IsProtectedDokanPath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        // Dokan paths are backslash-prefixed and absolute within the drive.
        // We treat \{controlFolder} and anything inside it as protected.
        var normalized = fileName.Replace('/', '\\').TrimStart('\\');
        var folder = ProtectedFolderName.Trim('/').Replace('/', '\\');
        if (string.Equals(normalized, folder, StringComparison.OrdinalIgnoreCase)) return true;
        return normalized.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public void SetHydration(IHydrationTrigger hydration)
    {
        // late-binding setter not used; constructor injection preferred
    }

    private string DokanToPosixRelative(string dokanPath)
    {
        var p = (dokanPath ?? "").Replace('\\', '/');
        if (string.IsNullOrEmpty(p)) return "/";
        if (!p.StartsWith('/')) p = "/" + p;
        return p;
    }

    private void EnsureHydratedSync(string dokanPath, string localPath)
    {
        if (_hydration is null) return;
        var rel = DokanToPosixRelative(dokanPath);
        if (rel == "/" || rel.Length == 0) return;

        // If Graph is throttled, we'd otherwise block this Dokan thread for up
        // to 10 minutes inside WaitForCooldownAsync — long enough for Explorer
        // to mark the operation "Not Responding" and for the user to kill it.
        // Instead: kick off the hydration in the background so it completes
        // when the cooldown lifts, and return immediately. Explorer's ReadFile
        // will see a 0-byte file (the placeholder) which is unfortunate but
        // strictly better than a 10-minute hang. Subsequent reads after
        // hydration completes will return real content.
        if (_hydration.IsGraphInCooldown)
        {
            _logger.Debug("Hydration deferred (Graph in cooldown) for {Path}", rel);
            var hydrationRef = _hydration;
            var driveRef = _driveConfig;
            _ = Task.Run(() =>
            {
                try { hydrationRef.HydrateIfNeeded(driveRef, rel, localPath); }
                catch (Exception ex) { _logger.Debug(ex, "Deferred hydration failed for {Path}", rel); }
            });
            return;
        }

        try
        {
            _hydration.HydrateIfNeeded(_driveConfig, rel, localPath);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Hydration trigger failed for {Path}", rel);
        }
    }

    public DriveConfig DriveConfig => _driveConfig;

    private string GetLocalPath(string dokanPath)
    {
        // Dokan gives \Documents\file.txt - strip the leading \ and combine
        var rel = dokanPath?.TrimStart('\\') ?? string.Empty;
        return string.IsNullOrEmpty(rel) ? _localRoot : Path.Combine(_localRoot, rel);
    }

    private static bool IsRecycleBinPath(string fileName) =>
        fileName.Contains("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase);

    public NtStatus CreateFile(
        string fileName, FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        if (IsRecycleBinPath(fileName))
            return DokanResult.PathNotFound;

        var localPath = GetLocalPath(fileName);
        var pathExists = false;
        var pathIsDirectory = false;
        try
        {
            pathExists = Directory.Exists(localPath) || File.Exists(localPath);
            pathIsDirectory = pathExists && Directory.Exists(localPath);
        }
        catch
        {
            // fall through, will fail naturally below
        }

        var readWriteAttributes = (access & DataAccess) == 0;
        var readAccess = (access & DataWriteAccess) == 0;

        if (info.IsDirectory)
        {
            try
            {
                switch (mode)
                {
                    case FileMode.Open:
                        if (!Directory.Exists(localPath))
                        {
                            return File.Exists(localPath)
                                ? DokanResult.NotADirectory
                                : DokanResult.PathNotFound;
                        }
                        // No file handle for directories
                        new DirectoryInfo(localPath).EnumerateFileSystemInfos().Any();
                        break;

                    case FileMode.CreateNew:
                        if (Directory.Exists(localPath)) return DokanResult.FileExists;
                        try
                        {
                            File.GetAttributes(localPath);
                            return DokanResult.AlreadyExists;
                        }
                        catch (IOException) { /* normal: doesn't exist */ }
                        Directory.CreateDirectory(localPath);
                        break;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return DokanResult.AccessDenied;
            }
            return DokanResult.Success;
        }

        // File operation
        var pathIsDir = pathExists && pathIsDirectory;
        var result = DokanResult.Success;

        // Hydration check: if the user is opening an existing placeholder file
        // for read or readwrite, download remote content before we open the
        // local FileStream. This is the only chance to atomically replace the
        // local file - once we hold an open handle, atomic replacement is blocked.
        if (pathExists && !pathIsDir && _hydration != null &&
            (mode == FileMode.Open || mode == FileMode.OpenOrCreate))
        {
            try
            {
                var localInfo = new FileInfo(localPath);
                if (localInfo.Length == 0)
                {
                    EnsureHydratedSync(fileName, localPath);
                }

                // Touch the LRU access time so the eviction service won't
                // pick this file as a victim while the user is working with it.
                // Cheap: one indexed LiteDB update per file open.
                try
                {
                    var rel = DokanToPosixRelative(fileName);
                    if (rel != "/" && rel.Length > 0)
                        _hydration.NotifyAccessed(_driveConfig, rel);
                }
                catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Pre-open hydration probe failed for {Path}", localPath);
            }
        }

        switch (mode)
        {
            case FileMode.Open:
                if (pathExists)
                {
                    if (readWriteAttributes || pathIsDir)
                    {
                        if (pathIsDir && (access & FileAccess.Delete) == FileAccess.Delete
                            && (access & FileAccess.Synchronize) != FileAccess.Synchronize)
                            return DokanResult.AccessDenied;
                        info.IsDirectory = pathIsDir;
                        info.Context = new object();
                        return DokanResult.Success;
                    }
                }
                else
                {
                    return DokanResult.FileNotFound;
                }
                break;

            case FileMode.CreateNew:
                if (pathExists) return DokanResult.FileExists;
                break;

            case FileMode.Truncate:
                if (!pathExists) return DokanResult.FileNotFound;
                break;
        }

        try
        {
            var streamAccess = readAccess ? System.IO.FileAccess.Read : System.IO.FileAccess.ReadWrite;
            if (mode == FileMode.CreateNew && readAccess)
                streamAccess = System.IO.FileAccess.ReadWrite;

            var stream = new FileStream(localPath, mode, streamAccess, share, 4096, options);

            if (pathExists && (mode == FileMode.OpenOrCreate || mode == FileMode.Create))
                result = DokanResult.AlreadyExists;

            var fileCreated = mode == FileMode.CreateNew
                || mode == FileMode.Create
                || (!pathExists && mode == FileMode.OpenOrCreate);
            if (fileCreated)
            {
                FileAttributes newAttrs = attributes;
                newAttrs |= FileAttributes.Archive;
                newAttrs &= ~FileAttributes.Normal;
                File.SetAttributes(localPath, newAttrs);
            }

            info.Context = stream;
        }
        catch (UnauthorizedAccessException)
        {
            return DokanResult.AccessDenied;
        }
        catch (DirectoryNotFoundException)
        {
            return DokanResult.PathNotFound;
        }
        catch (FileNotFoundException)
        {
            return DokanResult.FileNotFound;
        }
        catch (IOException ioex) when (ioex.HResult == unchecked((int)0x80070050)) // file exists
        {
            return DokanResult.FileExists;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "CreateFile failed for {Path} mode={Mode} access={Access}", localPath, mode, access);
            return DokanResult.Error;
        }

        return result;
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        CloseHandle(info);

        if (info.DeleteOnClose)
        {
            var localPath = GetLocalPath(fileName);
            try
            {
                if (info.IsDirectory) Directory.Delete(localPath);
                else if (File.Exists(localPath)) File.Delete(localPath);
                _onLocalChange?.Invoke(localPath, WatcherChangeTypes.Deleted);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "DeleteOnClose failed for {Path}", localPath);
            }
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        CloseHandle(info);
    }

    private void CloseHandle(IDokanFileInfo info)
    {
        if (info.Context is FileStream fs)
        {
            try { fs.Dispose(); } catch { }
        }
        info.Context = null;
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        var localPath = GetLocalPath(fileName);

        // Hydrate on first read: if the local file is zero-length but metadata says
        // it has remote content, download it before serving the read.
        if (offset == 0 && _hydration != null)
        {
            try
            {
                var fi = new FileInfo(localPath);
                if (fi.Exists && fi.Length == 0)
                    EnsureHydratedSync(fileName, localPath);
            }
            catch { /* ignore */ }
        }

        if (info.Context is FileStream fs)
        {
            // If we hydrated, the stream's file may have been replaced - reopen
            try
            {
                lock (fs)
                {
                    fs.Position = offset;
                    bytesRead = fs.Read(buffer, 0, buffer.Length);
                }
                if (bytesRead > 0 || offset >= fs.Length)
                    return DokanResult.Success;
            }
            catch
            {
                // stream invalid after hydrate - fall through to reopen
            }
        }

        try
        {
            using var stream = new FileStream(localPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            stream.Position = offset;
            bytesRead = stream.Read(buffer, 0, buffer.Length);
            return DokanResult.Success;
        }
        catch (FileNotFoundException)
        {
            bytesRead = 0;
            return DokanResult.FileNotFound;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ReadFile failed for {Path}", localPath);
            bytesRead = 0;
            return DokanResult.Error;
        }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        if (info.Context is FileStream fs)
        {
            lock (fs)
            {
                fs.Position = offset;
                fs.Write(buffer, 0, buffer.Length);
            }
            bytesWritten = buffer.Length;
            return DokanResult.Success;
        }

        var localPath = GetLocalPath(fileName);
        try
        {
            using var stream = new FileStream(localPath, FileMode.OpenOrCreate, System.IO.FileAccess.Write, FileShare.ReadWrite);
            stream.Position = offset;
            stream.Write(buffer, 0, buffer.Length);
            bytesWritten = buffer.Length;
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WriteFile failed for {Path}", localPath);
            bytesWritten = 0;
            return DokanResult.Error;
        }
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        if (info.Context is FileStream fs)
        {
            try
            {
                fs.Flush();
                return DokanResult.Success;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Flush failed for {Path}", fileName);
                return DokanResult.DiskFull;
            }
        }
        return DokanResult.Success;
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        var localPath = GetLocalPath(fileName);
        try
        {
            FileSystemInfo fsi = Directory.Exists(localPath)
                ? new DirectoryInfo(localPath)
                : new FileInfo(localPath);
            if (!fsi.Exists)
            {
                fileInfo = default;
                return DokanResult.FileNotFound;
            }

            long length = fsi is FileInfo fi ? fi.Length : 0L;

            // For placeholders (zero-length local files), report the remote size so Explorer's
            // size column matches the cloud file.
            if (fsi is FileInfo file && file.Length == 0 && _hydration != null)
            {
                var remoteSize = _hydration.GetRemoteSize(_driveConfig, DokanToPosixRelative(fileName));
                if (remoteSize > 0) length = remoteSize;
            }

            var attrs = fsi.Attributes;
            // Mirror FindFilesWithPattern: any path inside the control folder
            // (or the control folder itself at the drive root) gets Hidden+System.
            if (IsProtectedDokanPath(fileName))
            {
                attrs |= FileAttributes.Hidden | FileAttributes.System;
            }

            fileInfo = new FileInformation
            {
                FileName = fsi.Name,
                Attributes = attrs,
                CreationTime = fsi.CreationTime,
                LastAccessTime = fsi.LastAccessTime,
                LastWriteTime = fsi.LastWriteTime,
                Length = length,
            };
            return DokanResult.Success;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "GetFileInformation failed for {Path}", localPath);
            fileInfo = default;
            return DokanResult.Error;
        }
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        return FindFilesWithPattern(fileName, "*", out files, info);
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern,
        out IList<FileInformation> files, IDokanFileInfo info)
    {
        var localPath = GetLocalPath(fileName);
        files = new List<FileInformation>();
        try
        {
            if (!Directory.Exists(localPath))
                return DokanResult.PathNotFound;

            var dirInfo = new DirectoryInfo(localPath);
            var parentRel = DokanToPosixRelative(fileName);

            if (_hydration != null)
            {
                if (_hydration.EnumerateFolderIfEmpty(_driveConfig, parentRel))
                    dirInfo = new DirectoryInfo(localPath);
            }

            foreach (var item in dirInfo.EnumerateFileSystemInfos())
            {
                if (item.Name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!DokanHelper.DokanIsNameInExpression(searchPattern, item.Name, ignoreCase: true))
                    continue;

                long length = item is FileInfo fi ? fi.Length : 0L;

                // Patch placeholder size from metadata
                if (item is FileInfo file && file.Length == 0 && _hydration != null)
                {
                    var childRel = (parentRel == "/" ? "/" : parentRel + "/") + item.Name;
                    var remoteSize = _hydration.GetRemoteSize(_driveConfig, childRel);
                    if (remoteSize > 0) length = remoteSize;
                }

                var attrs = item.Attributes;
                // Mark the OneSync control folder Hidden+System so users only see it
                // when "Show hidden files" AND "Show protected operating system files"
                // are both enabled. Users with default settings won't notice it.
                if (parentRel == "/" && string.Equals(item.Name, ProtectedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    attrs |= FileAttributes.Hidden | FileAttributes.System;
                }

                files.Add(new FileInformation
                {
                    FileName = item.Name,
                    Attributes = attrs,
                    CreationTime = item.CreationTime,
                    LastAccessTime = item.LastAccessTime,
                    LastWriteTime = item.LastWriteTime,
                    Length = length,
                });
            }
            return DokanResult.Success;
        }
        catch (UnauthorizedAccessException) { return DokanResult.AccessDenied; }
        catch (Exception ex)
        {
            _logger.Debug(ex, "FindFiles failed for {Path}", localPath);
            return DokanResult.Error;
        }
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        var localPath = GetLocalPath(fileName);
        try
        {
            if (attributes != 0)
                File.SetAttributes(localPath, attributes);
            return DokanResult.Success;
        }
        catch (FileNotFoundException) { return DokanResult.FileNotFound; }
        catch (UnauthorizedAccessException) { return DokanResult.AccessDenied; }
        catch (Exception) { return DokanResult.Error; }
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
        DateTime? lastWriteTime, IDokanFileInfo info)
    {
        var localPath = GetLocalPath(fileName);
        try
        {
            if (info.Context is FileStream)
            {
                // Setting times on an open file via path is fine; FileStream supports it on close
            }
            if (creationTime.HasValue) File.SetCreationTime(localPath, creationTime.Value);
            if (lastAccessTime.HasValue) File.SetLastAccessTime(localPath, lastAccessTime.Value);
            if (lastWriteTime.HasValue) File.SetLastWriteTime(localPath, lastWriteTime.Value);
            return DokanResult.Success;
        }
        catch (FileNotFoundException) { return DokanResult.FileNotFound; }
        catch (UnauthorizedAccessException) { return DokanResult.AccessDenied; }
        catch (Exception) { return DokanResult.Error; }
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        if (IsProtectedDokanPath(fileName))
        {
            _logger.Information("Refused DeleteFile inside protected control folder: {Path}", fileName);
            return DokanResult.AccessDenied;
        }
        var localPath = GetLocalPath(fileName);
        if (Directory.Exists(localPath)) return DokanResult.AccessDenied;
        if (!File.Exists(localPath)) return DokanResult.FileNotFound;
        // Actual delete happens in Cleanup with DeleteOnClose
        return DokanResult.Success;
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        if (IsProtectedDokanPath(fileName))
        {
            _logger.Information("Refused DeleteDirectory inside protected control folder: {Path}", fileName);
            return DokanResult.AccessDenied;
        }
        var localPath = GetLocalPath(fileName);
        if (!Directory.Exists(localPath)) return DokanResult.PathNotFound;
        try
        {
            if (Directory.EnumerateFileSystemEntries(localPath).Any())
                return DokanResult.DirectoryNotEmpty;
            return DokanResult.Success;
        }
        catch (Exception) { return DokanResult.Error; }
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        if (IsProtectedDokanPath(oldName) || IsProtectedDokanPath(newName))
        {
            _logger.Information("Refused MoveFile involving protected control folder: {Old} -> {New}", oldName, newName);
            return DokanResult.AccessDenied;
        }
        var oldPath = GetLocalPath(oldName);
        var newPath = GetLocalPath(newName);

        CloseHandle(info);

        try
        {
            var existsAsFile = File.Exists(newPath);
            var existsAsDir = Directory.Exists(newPath);

            if (info.IsDirectory)
            {
                if (existsAsDir || existsAsFile)
                {
                    if (!replace) return DokanResult.FileExists;
                    if (existsAsFile) return DokanResult.AccessDenied;
                    Directory.Delete(newPath, true);
                }
                Directory.Move(oldPath, newPath);
            }
            else
            {
                if (existsAsFile)
                {
                    if (!replace) return DokanResult.FileExists;
                    File.Delete(newPath);
                }
                File.Move(oldPath, newPath);
            }
            _onLocalChange?.Invoke(newPath, WatcherChangeTypes.Renamed);
            return DokanResult.Success;
        }
        catch (UnauthorizedAccessException) { return DokanResult.AccessDenied; }
        catch (FileNotFoundException) { return DokanResult.FileNotFound; }
        catch (DirectoryNotFoundException) { return DokanResult.PathNotFound; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "MoveFile {Old} -> {New} failed", oldPath, newPath);
            return DokanResult.Error;
        }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (info.Context is FileStream fs)
        {
            try
            {
                fs.SetLength(length);
                return DokanResult.Success;
            }
            catch (IOException) { return DokanResult.DiskFull; }
        }
        return DokanResult.InvalidHandle;
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        if (info.Context is FileStream fs)
        {
            try
            {
                fs.SetLength(length);
                return DokanResult.Success;
            }
            catch (IOException) { return DokanResult.DiskFull; }
        }
        return DokanResult.InvalidHandle;
    }

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        if (info.Context is FileStream fs)
        {
            try
            {
                fs.Lock(offset, length);
                return DokanResult.Success;
            }
            catch (IOException) { return DokanResult.AccessDenied; }
        }
        return DokanResult.InvalidHandle;
    }

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        if (info.Context is FileStream fs)
        {
            try
            {
                fs.Unlock(offset, length);
                return DokanResult.Success;
            }
            catch (IOException) { return DokanResult.AccessDenied; }
        }
        return DokanResult.InvalidHandle;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        var quota = _quotaCache.GetCached(_driveConfig);
        if (quota.TotalBytes > 0)
        {
            freeBytesAvailable = quota.RemainingBytes;
            totalNumberOfBytes = quota.TotalBytes;
            totalNumberOfFreeBytes = quota.RemainingBytes;
        }
        else
        {
            // Fallback to local disk free space until cloud quota arrives
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(_localRoot) ?? "C:\\");
                freeBytesAvailable = driveInfo.AvailableFreeSpace;
                totalNumberOfBytes = driveInfo.TotalSize;
                totalNumberOfFreeBytes = driveInfo.TotalFreeSpace;
            }
            catch
            {
                freeBytesAvailable = 0;
                totalNumberOfBytes = 0;
                totalNumberOfFreeBytes = 0;
            }
        }
        return DokanResult.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
        out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = _driveConfig.Label;
        fileSystemName = "NTFS";
        features = FileSystemFeatures.CasePreservedNames
            | FileSystemFeatures.CaseSensitiveSearch
            | FileSystemFeatures.PersistentAcls
            | FileSystemFeatures.SupportsRemoteStorage
            | FileSystemFeatures.UnicodeOnDisk;
        maximumComponentLength = 255;
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        var localPath = GetLocalPath(fileName);
        try
        {
            if (Directory.Exists(localPath))
                security = new DirectoryInfo(localPath).GetAccessControl(sections);
            else if (File.Exists(localPath))
                security = new FileInfo(localPath).GetAccessControl(sections);
            else
                return DokanResult.FileNotFound;
            return DokanResult.Success;
        }
        catch (UnauthorizedAccessException) { return DokanResult.AccessDenied; }
        catch (Exception) { return DokanResult.Error; }
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security,
        AccessControlSections sections, IDokanFileInfo info)
    {
        var localPath = GetLocalPath(fileName);
        try
        {
            if (Directory.Exists(localPath))
                new DirectoryInfo(localPath).SetAccessControl((DirectorySecurity)security);
            else if (File.Exists(localPath))
                new FileInfo(localPath).SetAccessControl((FileSecurity)security);
            else
                return DokanResult.FileNotFound;
            return DokanResult.Success;
        }
        catch (UnauthorizedAccessException) { return DokanResult.AccessDenied; }
        catch (Exception) { return DokanResult.Error; }
    }

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        _logger.Information("Drive {Letter}: ({Label}) mounted at {Mount}",
            _driveConfig.Letter, _driveConfig.Label, mountPoint);
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        _logger.Information("Drive {Letter}: ({Label}) unmounted",
            _driveConfig.Letter, _driveConfig.Label);
        return DokanResult.Success;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = new List<FileInformation>();
        return DokanResult.NotImplemented;
    }
}
