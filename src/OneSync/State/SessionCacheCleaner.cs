using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneSync.Config;
using OneSync.Sync;
using Serilog;

namespace OneSync.State;

/// <summary>
/// Starts each session with a small cache. Records for files that are only in
/// the cloud are dropped along with their placeholders, and folders are listed
/// again as the user opens them. Files held locally keep their records, so the
/// app still knows which of them have changes waiting to upload. Pending
/// uploads in sync_queue.db are always kept.
/// </summary>
internal static class SessionCacheCleaner
{
    /// <summary>Cache is considered stale (and wiped on startup regardless of
    /// shutdown cleanliness) after this many hours. Catches ungraceful exits.</summary>
    private const int StaleAfterHours = 12;

    /// <summary>sync_queue.db is compacted on every graceful shutdown. This is the
    /// fallback: if a marker file shows compaction hasn't run in this long,
    /// startup runs one. Catches the case where the user crashes repeatedly
    /// without ever shutting down cleanly.</summary>
    private const int SyncQueueCompactionMaxAgeDays = 7;

    public static void WipeIfApplicable(
        AppConfig config,
        bool previousShutdownWasClean,
        string stateDir,
        IEnumerable<DriveConfig> drives,
        ILogger logger)
    {
        if (!config.SyncSettings.SessionCacheMode)
        {
            logger.Debug("SessionCacheMode disabled - skipping session-start wipe");
            return;
        }

        var metadataPath = Path.Combine(stateDir, "metadata.db");
        if (!File.Exists(metadataPath))
        {
            logger.Debug("No metadata.db present - nothing to wipe");
            return;
        }

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(metadataPath);
        var isStale = age > TimeSpan.FromHours(StaleAfterHours);

        if (!previousShutdownWasClean && !isStale)
        {
            logger.Information(
                "Session cache kept: previous shutdown was unclean and cache age {Hours:F1}h < {Limit}h (likely a quick restart)",
                age.TotalHours, StaleAfterHours);
            return;
        }

        var reason = previousShutdownWasClean
            ? "previous session exited cleanly"
            : $"cache stale ({age.TotalHours:F1}h > {StaleAfterHours}h)";
        logger.Information("Wiping session cache: {Reason}", reason);

        // 1) Delete delta tokens from sync_queue.db (preserve pending sync_ops).
        //    Must run BEFORE deleting metadata.db so we know SyncQueue can still
        //    open its own file - they live side by side but are independent DBs.
        try
        {
            var syncQueuePath = Path.Combine(stateDir, "sync_queue.db");
            if (File.Exists(syncQueuePath))
            {
                using var q = new SyncQueue(syncQueuePath, logger);
                var dropped = q.ClearAllDeltaTokens();
                logger.Information("Cleared {Count} delta tokens (pending uploads preserved)", dropped);
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Failed clearing delta tokens - continuing wipe");
        }

        // 2) Drop cloud-only placeholders and their records; keep everything
        //    with content on this PC.
        try
        {
            using var metadata = new MetadataStore(metadataPath, logger);
            foreach (var drive in drives)
            {
                if (string.IsNullOrEmpty(drive.LocalRootPath) || !Directory.Exists(drive.LocalRootPath)) continue;
                try { WipePlaceholdersUnder(drive, metadata, logger); }
                catch (Exception ex) { logger.Warning(ex, "Placeholder wipe failed under {Path}", drive.LocalRootPath); }
            }

            var pruned = metadata.PruneToLocalCopies();
            metadata.ClearAllEnumerationState();
            metadata.Compact();
            logger.Information("Session cache trimmed: {Count} cloud-only records dropped", pruned);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not trim metadata.db; starting with it as it is");
        }
    }

    private static void WipePlaceholdersUnder(DriveConfig drive, MetadataStore metadata, ILogger logger)
    {
        var root = drive.LocalRootPath;
        var filesDeleted = 0;
        var kept = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(file);
                if (LocalState.IsPlaceholder(fi) || IsOldStylePlaceholder(drive, metadata, root, fi) ||
                    LocalState.IsDownloadTemp(fi.FullName))
                {
                    fi.Delete();
                    filesDeleted++;
                }
                else
                {
                    kept++;
                }
            }
            catch { }
        }

        // Empty folders we know from the cloud are recreated when their parent is
        // listed. Empty folders with no cloud record were made here and may not
        // have been created in the cloud yet, so they stay.
        var dirsDeleted = 0;
        var subDirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();
        foreach (var dir in subDirs)
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                var meta = metadata.Get(drive.ConfigId, ToRelative(root, dir));
                if (meta is { IsFolder: true } && !string.IsNullOrEmpty(meta.RemoteItemId))
                {
                    Directory.Delete(dir);
                    dirsDeleted++;
                }
            }
            catch { }
        }

        logger.Information(
            "Session cache trimmed for {Letter}: {Placeholders} placeholders deleted, {Kept} local files kept, {EmptyDirs} empty folders removed",
            drive.Letter, filesDeleted, kept, dirsDeleted);
    }

    /// <summary>An empty, non-sparse file that our records say is a cloud-only
    /// file with content. Older versions made placeholders without the sparse flag.</summary>
    private static bool IsOldStylePlaceholder(DriveConfig drive, MetadataStore metadata, string root, FileInfo fi)
    {
        if (fi.Length != 0) return false;
        var meta = metadata.Get(drive.ConfigId, ToRelative(root, fi.FullName));
        return meta is { IsFolder: false, Hydrated: false } && meta.Size > 0 && !LocalState.HasBaseline(meta);
    }

    private static string ToRelative(string root, string fullPath) =>
        Util.PathUtil.NormalizeRelative(Path.GetRelativePath(root, fullPath));

    /// <summary>Defensive sync_queue.db compaction: if the last-compact marker is
    /// missing or older than <see cref="SyncQueueCompactionMaxAgeDays"/>, open
    /// sync_queue.db, compact it, and update the marker. Pending uploads survive
    /// (compaction defragments, doesn't delete). Catches users who crash
    /// repeatedly and never reach the GracefulShutdown compaction path.</summary>
    public static void CompactSyncQueueIfStale(string stateDir, ILogger logger)
    {
        var syncQueuePath = Path.Combine(stateDir, "sync_queue.db");
        if (!File.Exists(syncQueuePath)) return;

        var marker = Path.Combine(stateDir, "sync_queue.last-compact");
        var needsCompact = !File.Exists(marker);
        if (!needsCompact)
        {
            try
            {
                var lastCompact = File.GetLastWriteTimeUtc(marker);
                var age = DateTime.UtcNow - lastCompact;
                needsCompact = age > TimeSpan.FromDays(SyncQueueCompactionMaxAgeDays);
                if (needsCompact)
                    logger.Information(
                        "sync_queue.db compaction overdue: last ran {Days:F1} days ago (>{Limit}d) - compacting on startup",
                        age.TotalDays, SyncQueueCompactionMaxAgeDays);
            }
            catch
            {
                needsCompact = true; // can't read marker - treat as stale
            }
        }
        else
        {
            logger.Information("sync_queue.db has no compaction marker - running first compaction on startup");
        }

        if (!needsCompact) return;

        try
        {
            var sizeBefore = new FileInfo(syncQueuePath).Length;
            using (var q = new SyncQueue(syncQueuePath, logger))
            {
                q.Compact();
            }
            var sizeAfter = new FileInfo(syncQueuePath).Length;
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            logger.Information(
                "sync_queue.db compacted: {Before} -> {After} bytes ({DeltaPercent:F0}% reclaimed)",
                sizeBefore, sizeAfter,
                sizeBefore > 0 ? (sizeBefore - sizeAfter) * 100.0 / sizeBefore : 0);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Defensive sync_queue.db compaction failed");
        }
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                logger.Debug("Deleted {Path}", path);
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not delete {Path}", path);
        }
    }
}
