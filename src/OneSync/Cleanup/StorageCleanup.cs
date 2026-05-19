using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OneSync.Config;
using OneSync.Util;
using Serilog;

namespace OneSync.Cleanup;

internal sealed class StorageCleanup
{
    private const string MarkerFileName = ".last_user";
    private const string CleanShutdownMarker = ".clean_shutdown";
    private static readonly string[] ProtectedRootEntries =
    {
        MarkerFileName,
        CleanShutdownMarker,
        "auth_cache.bin",
        "Logs",
        "quota_cache.json",
        "config.json",
    };

    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly string _currentUser;

    public StorageCleanup(AppConfig config, ILogger logger, string? currentUser = null)
    {
        _config = config;
        _logger = logger;
        _currentUser = currentUser ?? Environment.UserName;
    }

    public void RunLogonCleanup()
    {
        var storageRoot = _config.LocalStorageRoot;
        Directory.CreateDirectory(storageRoot);
        var markerFile = Path.Combine(storageRoot, MarkerFileName);

        bool shouldClean = false;
        string reason = "";

        if (File.Exists(markerFile))
        {
            string lastUser;
            try
            {
                lastUser = File.ReadAllText(markerFile).Trim();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not read {Marker}, treating as stale", markerFile);
                lastUser = "<unreadable>";
            }

            if (!lastUser.Equals(_currentUser, StringComparison.OrdinalIgnoreCase))
            {
                shouldClean = true;
                reason = $"previous user was '{lastUser}', current user is '{_currentUser}'";
            }
            else if (WasPreviousShutdownDirty(storageRoot) &&
                     HasResidualDriveStorage(storageRoot))
            {
                _logger.Information(
                    "Previous session did not shut down cleanly — keeping existing placeholders (delta poller will reconcile)");
            }
        }
        else
        {
            _logger.Information("First run for user '{User}' - no .last_user marker present", _currentUser);
        }

        if (shouldClean)
        {
            _logger.Information("Logon cleanup running: {Reason}", reason);
            DeleteDriveStorage(storageRoot);
        }
        else
        {
            _logger.Debug("Logon cleanup not needed (user '{User}' matches marker)", _currentUser);
        }

        TryWriteMarker(markerFile);
    }

    public void RunLogoffCleanup(IReadOnlyCollection<string>? preserveLocalPaths = null)
    {
        if (!_config.Cleanup.CleanOnLogoff)
        {
            _logger.Information("Logoff cleanup disabled - leaving local storage intact");
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(0, _config.Cleanup.CleanupDelayAfterSyncFlushSeconds));
        if (delay > TimeSpan.Zero)
        {
            _logger.Information("Waiting {Delay:c} for file handles to release before cleanup", delay);
            Thread.Sleep(delay);
        }

        var preserveSet = BuildPreserveSet(preserveLocalPaths);
        _logger.Information("Logoff cleanup running for user '{User}' (preserving {Count} files with pending uploads)",
            _currentUser, preserveSet.Count);
        DeleteDriveStorage(_config.LocalStorageRoot, preserveSet);
    }

    private HashSet<string> BuildPreserveSet(IReadOnlyCollection<string>? paths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths == null) return set;
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            set.Add(Path.GetFullPath(p));
            // Also preserve every ancestor directory so the file survives parent-dir deletion
            var dir = Path.GetDirectoryName(Path.GetFullPath(p));
            while (!string.IsNullOrEmpty(dir))
            {
                set.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        return set;
    }

    public void MarkSessionStarted()
    {
        // Remove the clean_shutdown marker - we'll write it again on graceful exit.
        var path = Path.Combine(_config.LocalStorageRoot, CleanShutdownMarker);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>
    /// Records that the process is exiting via a graceful path (Application.Run
    /// returned, OS sent SessionEnding, etc.). Decoupled from RunLogoffCleanup
    /// so the marker gets written BEFORE the storage wipe — Windows kills the
    /// process within ~20s of logoff, often before the wipe completes, and
    /// without this decoupling the marker is never written.
    /// </summary>
    public void MarkCleanShutdown()
    {
        try
        {
            Directory.CreateDirectory(_config.LocalStorageRoot);
            var path = Path.Combine(_config.LocalStorageRoot, CleanShutdownMarker);
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not write clean_shutdown marker");
        }
    }

    private bool WasPreviousShutdownDirty(string storageRoot)
    {
        var marker = Path.Combine(storageRoot, CleanShutdownMarker);
        return !File.Exists(marker);
    }

    /// <summary>True if the last process exit wrote a clean_shutdown marker.
    /// Must be called BEFORE MarkSessionStarted (which deletes the marker).</summary>
    public bool WasPreviousShutdownClean()
    {
        var marker = Path.Combine(_config.LocalStorageRoot, CleanShutdownMarker);
        return File.Exists(marker);
    }

    private bool HasResidualDriveStorage(string storageRoot)
    {
        try
        {
            return Directory.GetDirectories(storageRoot).Any();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not enumerate {Root}", storageRoot);
            return false;
        }
    }

    private void DeleteDriveStorage(string storageRoot, HashSet<string>? preserveSet = null)
    {
        if (!Directory.Exists(storageRoot))
        {
            _logger.Debug("Storage root does not exist: {Root}", storageRoot);
            return;
        }

        // Delete drive subdirectories (typically HomeDrive, StudentShared, etc.)
        foreach (var dir in Directory.GetDirectories(storageRoot))
        {
            var name = Path.GetFileName(dir);
            if (_config.Cleanup.PreserveLogs && string.Equals(name, "Logs", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("Preserving Logs directory");
                continue;
            }

            if (preserveSet != null && preserveSet.Count > 0)
            {
                // Selective delete - keep any files/dirs in preserveSet
                DeleteTreeExceptPreserved(dir, preserveSet);
            }
            else
            {
                TryDeleteDirectory(dir);
            }
        }

        // Delete cached state files in the root
        foreach (var file in Directory.GetFiles(storageRoot))
        {
            var name = Path.GetFileName(file);

            if (string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(name, CleanShutdownMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_config.Cleanup.PreserveAuthCache &&
                string.Equals(name, "auth_cache.bin", StringComparison.OrdinalIgnoreCase))
                continue;

            if (_config.Cleanup.PreserveQuotaCache &&
                string.Equals(name, "quota_cache.json", StringComparison.OrdinalIgnoreCase))
                continue;

            // sync queue, delta tokens, etc.
            TryDeleteFile(file);
        }

        // NOTE: we deliberately do NOT delete sync_queue.db, metadata.db, or their
        // LiteDB write-ahead-log files (*-log.db). These persist across sessions so:
        //   - pending uploads (sync queue) can resume after an interrupted shutdown
        //   - delta tokens + remote metadata avoid expensive full re-sync on each logon
        // The placeholder files inside the Drives folder are wiped; metadata is enough
        // to recreate them in seconds via RebuildMissingPlaceholders.

        _logger.Information("Storage cleanup complete (drive placeholders wiped, DB state preserved)");
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            _logger.Debug("Deleted directory: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not delete {Path}", path);
        }
    }

    private void DeleteTreeExceptPreserved(string root, HashSet<string> preserveSet)
    {
        try
        {
            // Files first
            foreach (var file in Directory.GetFiles(root))
            {
                var full = Path.GetFullPath(file);
                if (preserveSet.Contains(full))
                {
                    _logger.Debug("Preserving file (has pending upload): {Path}", full);
                    continue;
                }
                TryDeleteFile(file);
            }

            foreach (var subdir in Directory.GetDirectories(root))
            {
                var full = Path.GetFullPath(subdir);
                if (preserveSet.Contains(full))
                {
                    DeleteTreeExceptPreserved(subdir, preserveSet);
                }
                else
                {
                    TryDeleteDirectory(subdir);
                }
            }

            // If this directory is now empty AND not in preserveSet, remove it
            try
            {
                if (!preserveSet.Contains(Path.GetFullPath(root)) &&
                    Directory.GetFileSystemEntries(root).Length == 0)
                {
                    Directory.Delete(root);
                }
            }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "DeleteTreeExceptPreserved failed for {Root}", root);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.Debug("Deleted file: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not delete {Path}", path);
        }
    }

    private void TryWriteMarker(string markerPath)
    {
        try
        {
            File.WriteAllText(markerPath, _currentUser);
            _logger.Debug("Wrote .last_user marker for '{User}'", _currentUser);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not write marker file {Path}", markerPath);
        }
    }
}
