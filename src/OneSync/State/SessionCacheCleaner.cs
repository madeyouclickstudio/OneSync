using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OneSync.Config;
using OneSync.Sync;
using Serilog;

namespace OneSync.State;

/// <summary>
/// "Session cache" lifecycle: the per-user metadata.db is treated as session-scoped
/// rather than persistent. On startup, if the previous session ended gracefully
/// (or the cache is older than the staleness threshold), wipe metadata.db, drop
/// every drive's delta token, and remove 0-byte placeholder files. Pending uploads
/// (sync_queue.db) and hydrated file content are always preserved.
///
/// Rationale: on persistent profiles (no roaming) the cache grows unbounded as
/// LiteDB fragments under churn — observed at ~895 MB for one user. Wiping it
/// each session bounds disk usage. Lazy-fallback rebuilds whatever the user
/// actually touches; everything else stays out of the cache by design.
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
            logger.Debug("SessionCacheMode disabled — skipping session-start wipe");
            return;
        }

        var metadataPath = Path.Combine(stateDir, "metadata.db");
        var metadataLogPath = metadataPath + "-log";
        if (!File.Exists(metadataPath))
        {
            logger.Debug("No metadata.db present — nothing to wipe");
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
        //    open its own file — they live side by side but are independent DBs.
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
            logger.Warning(ex, "Failed clearing delta tokens — continuing wipe");
        }

        // 2) Delete metadata.db (LiteDB will create a fresh one when SyncEngine opens it).
        TryDelete(metadataPath, logger);
        TryDelete(metadataLogPath, logger);

        // 3) Walk each drive's local backing dir and remove 0-byte placeholders +
        //    empty directories left behind. Hydrated files (any non-zero length)
        //    are preserved so the user keeps their offline content.
        foreach (var drive in drives)
        {
            if (string.IsNullOrEmpty(drive.LocalRootPath)) continue;
            if (!Directory.Exists(drive.LocalRootPath)) continue;
            try
            {
                WipePlaceholdersUnder(drive, logger);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Placeholder wipe failed under {Path}", drive.LocalRootPath);
            }
        }
    }

    private static void WipePlaceholdersUnder(DriveConfig drive, ILogger logger)
    {
        var root = drive.LocalRootPath;
        var filesDeleted = 0;
        var hydratedKept = 0;

        // Phase 1: delete 0-byte files (placeholders). Hydrated files have content
        // (>0 bytes for any real Office doc, image, etc.) and are kept.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length == 0)
                {
                    fi.Delete();
                    filesDeleted++;
                }
                else
                {
                    hydratedKept++;
                }
            }
            catch
            {
                // Per-file failures (file locked, permission denied, etc.) are non-fatal —
                // a single stuck file shouldn't abort the wipe for the rest of the tree.
            }
        }

        // Phase 2: remove now-empty directories, deepest first. Preserve the drive
        // root itself even if empty (Dokan / mount logic needs it to exist).
        var dirsDeleted = 0;
        var subDirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();
        foreach (var dir in subDirs)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dirsDeleted++;
                }
            }
            catch
            {
                // Same as files — non-fatal.
            }
        }

        logger.Information(
            "Session cache wiped for {Letter}: {LocalRoot} — {Placeholders} placeholders deleted, {Hydrated} hydrated files kept, {EmptyDirs} empty dirs removed",
            drive.Letter, root, filesDeleted, hydratedKept, dirsDeleted);
    }

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
                        "sync_queue.db compaction overdue: last ran {Days:F1} days ago (>{Limit}d) — compacting on startup",
                        age.TotalDays, SyncQueueCompactionMaxAgeDays);
            }
            catch
            {
                needsCompact = true; // can't read marker — treat as stale
            }
        }
        else
        {
            logger.Information("sync_queue.db has no compaction marker — running first compaction on startup");
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
                "sync_queue.db compacted: {Before} → {After} bytes ({DeltaPercent:F0}% reclaimed)",
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
