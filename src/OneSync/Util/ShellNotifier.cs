using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OneSync.Config;

namespace OneSync.Util;

/// <summary>
/// Wraps Win32 SHChangeNotify so the rest of the codebase can call
/// "I just changed this file" / "I just added this file" / "the icon for
/// this file changed" without dealing with the P/Invoke surface.
///
/// Explorer doesn't auto-watch every folder — it listens for SHChangeNotify
/// broadcasts and refreshes only the views that match. Without these calls,
/// users have to F5 / right-click → Refresh to see new placeholders, the
/// sync overlay icon transitions, or the size-after-hydration update.
///
/// Each notification fires TWICE — once for the local NTFS path (where
/// PlaceholderManager / HydrationService actually wrote) and once for the
/// Dokan-mapped drive-letter path (where the user is looking in Explorer).
/// Both have to be notified separately because Windows doesn't know they're
/// the same file.
/// </summary>
internal static class ShellNotifier
{
    private static IReadOnlyList<DriveConfig>? _drives;

    /// <summary>Wire the configured drives so static callers (e.g.
    /// SyncStateMarker) can resolve a local NTFS path back to a (drive,
    /// relative) pair and notify both Explorer views. Called once at
    /// startup by Program.cs.</summary>
    public static void Initialize(IEnumerable<DriveConfig> drives)
    {
        _drives = new List<DriveConfig>(drives);
    }

    /// <summary>"This file's content / size / icon has changed." Variant for
    /// callers that only know the local NTFS path (e.g. SyncStateMarker).</summary>
    public static void NotifyUpdatedByLocalPath(string localPath)
    {
        if (_drives == null || string.IsNullOrEmpty(localPath)) return;
        foreach (var d in _drives)
        {
            if (string.IsNullOrEmpty(d.LocalRootPath)) continue;
            if (localPath.StartsWith(d.LocalRootPath, StringComparison.OrdinalIgnoreCase))
            {
                var rel = localPath.Substring(d.LocalRootPath.Length).TrimStart('\\', '/');
                var relPosix = "/" + rel.Replace('\\', '/');
                NotifyUpdated(d, relPosix);
                return;
            }
        }
    }

    /// <summary>"This file's content / size / icon has changed."</summary>
    public static void NotifyUpdated(DriveConfig drive, string relativePath)
    {
        NotifyBoth(SHCNE.UPDATEITEM, drive, relativePath);
    }

    /// <summary>"A new file or folder appeared at this path."</summary>
    public static void NotifyCreated(DriveConfig drive, string relativePath, bool isFolder = false)
    {
        var evt = isFolder ? SHCNE.MKDIR : SHCNE.CREATE;
        NotifyBoth(evt, drive, relativePath);
        // Also tell the parent directory its contents changed, so the
        // user-visible folder view repaints.
        NotifyParentDir(drive, relativePath);
    }

    /// <summary>"This file or folder was removed."</summary>
    public static void NotifyDeleted(DriveConfig drive, string relativePath, bool isFolder = false)
    {
        var evt = isFolder ? SHCNE.RMDIR : SHCNE.DELETE;
        NotifyBoth(evt, drive, relativePath);
        NotifyParentDir(drive, relativePath);
    }

    /// <summary>"This file or folder was renamed." Pass the OLD relative path
    /// and the NEW relative path. Both points get notified.</summary>
    public static void NotifyRenamed(DriveConfig drive, string oldRelativePath, string newRelativePath, bool isFolder = false)
    {
        var evt = isFolder ? SHCNE.RENAMEFOLDER : SHCNE.RENAMEITEM;
        var oldPaths = BuildBothPaths(drive, oldRelativePath);
        var newPaths = BuildBothPaths(drive, newRelativePath);
        foreach (var (oldP, newP) in Zip(oldPaths, newPaths))
            Notify(evt, oldP, newP);
        // Refresh both parent dirs (rename may have moved between folders)
        NotifyParentDir(drive, oldRelativePath);
        NotifyParentDir(drive, newRelativePath);
    }

    /// <summary>"This directory's contents changed (something was added/removed)."</summary>
    public static void NotifyDirChanged(DriveConfig drive, string relativeDirPath)
    {
        NotifyBoth(SHCNE.UPDATEDIR, drive, relativeDirPath);
    }

    // ---------- internals ----------

    private static void NotifyBoth(SHCNE evt, DriveConfig drive, string relativePath)
    {
        foreach (var p in BuildBothPaths(drive, relativePath))
            Notify(evt, p, null);
    }

    private static void NotifyParentDir(DriveConfig drive, string relativePath)
    {
        var parent = GetParentRelative(relativePath);
        foreach (var p in BuildBothPaths(drive, parent))
            Notify(SHCNE.UPDATEDIR, p, null);
    }

    private static IEnumerable<string> BuildBothPaths(DriveConfig drive, string relativePath)
    {
        // Same relative path expressed two ways. Explorer treats these as
        // different items so we have to notify both.
        var winRel = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        // 1) Local NTFS path (LocalRootPath\Documents\foo.docx)
        if (!string.IsNullOrEmpty(drive.LocalRootPath))
        {
            yield return string.IsNullOrEmpty(winRel)
                ? drive.LocalRootPath
                : Path.Combine(drive.LocalRootPath, winRel);
        }

        // 2) Drive-letter path (H:\Documents\foo.docx)
        if (!string.IsNullOrEmpty(drive.Letter))
        {
            yield return string.IsNullOrEmpty(winRel)
                ? drive.Letter.TrimEnd(':') + @":\"
                : drive.Letter.TrimEnd(':') + @":\" + winRel;
        }
    }

    private static string GetParentRelative(string relativePath)
    {
        var trimmed = relativePath.TrimEnd('/').TrimStart('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0) return "/";
        return "/" + trimmed.Substring(0, lastSlash);
    }

    private static IEnumerable<(string, string)> Zip(IEnumerable<string> a, IEnumerable<string> b)
    {
        using var ea = a.GetEnumerator();
        using var eb = b.GetEnumerator();
        while (ea.MoveNext() && eb.MoveNext())
            yield return (ea.Current, eb.Current);
    }

    private static void Notify(SHCNE evt, string path1, string? path2)
    {
        try
        {
            // SHCNF_PATHW = 0x0005 — both pointers are wide-string paths.
            // SHCNF_FLUSHNOWAIT = 0x2000 — fire-and-forget, don't block our thread.
            var flags = SHCNF.PATHW | SHCNF.FLUSHNOWAIT;
            var p1 = path1 != null ? Marshal.StringToHGlobalUni(path1) : IntPtr.Zero;
            var p2 = path2 != null ? Marshal.StringToHGlobalUni(path2) : IntPtr.Zero;
            try
            {
                SHChangeNotify((uint)evt, (uint)flags, p1, p2);
                System.Threading.Interlocked.Increment(ref _notifyCount);
                // Debug level: visible when troubleshooting with logging.level=Debug,
                // invisible in production logs.
                Serilog.Log.Debug("SHChangeNotify {Event} {Path1}{Path2}",
                    evt, path1, path2 != null ? " -> " + path2 : "");
            }
            finally
            {
                if (p1 != IntPtr.Zero) Marshal.FreeHGlobal(p1);
                if (p2 != IntPtr.Zero) Marshal.FreeHGlobal(p2);
            }
        }
        catch (Exception ex)
        {
            try { Serilog.Log.Warning(ex, "SHChangeNotify threw"); } catch { }
        }
    }

    private static long _notifyCount;
    public static long TotalNotifications => System.Threading.Interlocked.Read(ref _notifyCount);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // Subset of SHCNE_* constants we use.
    [Flags]
    private enum SHCNE : uint
    {
        RENAMEITEM   = 0x00000001,
        CREATE       = 0x00000002,
        DELETE       = 0x00000004,
        MKDIR        = 0x00000008,
        RMDIR        = 0x00000010,
        UPDATEDIR    = 0x00001000,
        UPDATEITEM   = 0x00002000,
        RENAMEFOLDER = 0x00020000,
    }

    [Flags]
    private enum SHCNF : uint
    {
        PATHW        = 0x0005,
        FLUSHNOWAIT  = 0x2000,
    }
}
