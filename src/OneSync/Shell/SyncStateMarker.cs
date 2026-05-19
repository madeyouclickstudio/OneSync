using System;
using System.IO;
using System.Threading;
using OneSync.Util;

namespace OneSync.Shell;

/// <summary>
/// Sync state values matching the C++ shell overlay handler's expectations.
/// </summary>
internal enum SyncOverlayState : byte
{
    CloudOnly = 0,
    Syncing = 1,
    Synced = 2,
    Error = 3,
}

/// <summary>
/// Writes a single-byte NTFS alternate data stream (filename:OneSync) on the
/// local NTFS file. The shell overlay DLL reads this and picks the matching
/// icon. Best-effort - failures are silent so a missing ADS just means no
/// overlay (not a sync failure).
/// </summary>
internal static class SyncStateMarker
{
    public const string AdsName = "OneSync";

    public static void Mark(string localPath, SyncOverlayState state)
    {
        if (string.IsNullOrEmpty(localPath)) return;
        if (!File.Exists(localPath)) return;

        // If the current overlay byte is already what we'd write, skip both
        // the file IO and the SHChangeNotify broadcast. This is the dominant
        // savings on startup: RebuildMissingPlaceholders re-marks every file
        // as CloudOnly even though it was already CloudOnly — we don't need
        // to ping Explorer thousands of times for no real change.
        var existing = Read(localPath);
        if (existing == state) return;

        var adsPath = $"{localPath}:{AdsName}";
        bool succeeded = false;
        try
        {
            // FileMode.Create truncates if exists - we always write exactly 1 byte
            using var fs = new FileStream(adsPath, FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.WriteByte((byte)state);
            succeeded = true;
        }
        catch (IOException)
        {
            // Locked by another process - try once more after a tiny pause
            try
            {
                Thread.Sleep(50);
                using var fs = new FileStream(adsPath, FileMode.Create,
                    FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.WriteByte((byte)state);
                succeeded = true;
            }
            catch { /* give up silently */ }
        }
        catch
        {
            // ADS not supported, access denied, etc. - silent
        }

        // If we changed the marker, tell Explorer the icon may have changed
        // so the overlay refreshes without the user pressing F5. Best-effort —
        // ShellNotifier swallows its own exceptions.
        if (succeeded)
        {
            ShellNotifier.NotifyUpdatedByLocalPath(localPath);
        }
    }

    public static SyncOverlayState? Read(string localPath)
    {
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return null;
        try
        {
            var adsPath = $"{localPath}:{AdsName}";
            using var fs = new FileStream(adsPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = fs.ReadByte();
            if (b < 0 || b > 3) return null;
            return (SyncOverlayState)b;
        }
        catch
        {
            return null;
        }
    }
}
