using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using OneSync.Config;
using Serilog;

namespace OneSync.FileSystem;

/// <summary>
/// Redirects Windows shell folders (Desktop, Documents, Downloads, etc.) to
/// subfolders of a mapped drive. Backs up the originals so they can be restored
/// on uninstall.
/// </summary>
internal sealed class FolderRedirector
{
    private const string UserShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
    private const string ShellFoldersKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";
    private const string BackupKey = @"Software\OneSync\OriginalShellFolders";
    private const string AppliedKey = @"Software\OneSync\AppliedRedirections";

    private static readonly Dictionary<string, string> FolderRegistryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"] = "Desktop",
            ["Documents"] = "Personal",
            ["Downloads"] = "{374DE290-123F-4565-9164-39C4925E467B}",
            ["Music"] = "My Music",
            ["Pictures"] = "My Pictures",
            ["Videos"] = "My Video",
        };

    // KNOWNFOLDERID GUIDs - https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid
    private static readonly Dictionary<string, Guid> KnownFolderIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"] = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
            ["Documents"] = new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
            ["Downloads"] = new Guid("374DE290-123F-4565-9164-39C4925E467B"),
            ["Music"] = new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"),
            ["Pictures"] = new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"),
            ["Videos"] = new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        };

    private readonly ILogger _logger;

    public FolderRedirector(ILogger logger) => _logger = logger;

    public void Apply(IEnumerable<DriveConfig> drives)
    {
        var applied = new List<string>();

        foreach (var drive in drives)
        {
            if (drive.FolderRedirection is null || drive.FolderRedirection.Count == 0)
                continue;

            using var hkcuUserShell = Registry.CurrentUser.CreateSubKey(UserShellFoldersKey);
            using var hkcuShell = Registry.CurrentUser.CreateSubKey(ShellFoldersKey);
            using var hkcuBackup = Registry.CurrentUser.CreateSubKey(BackupKey);
            using var hkcuApplied = Registry.CurrentUser.CreateSubKey(AppliedKey);

            if (hkcuUserShell is null || hkcuShell is null || hkcuBackup is null || hkcuApplied is null)
            {
                _logger.Warning("Could not open shell folders registry keys - skipping redirection");
                continue;
            }

            foreach (var folderName in drive.FolderRedirection)
            {
                if (!FolderRegistryNames.TryGetValue(folderName, out var regName))
                {
                    _logger.Warning("Unknown folder redirection target: {Name}", folderName);
                    continue;
                }

                var targetPath = $"{drive.Letter}:\\{folderName}";

                try
                {
                    Directory.CreateDirectory(targetPath);

                    // Back up the original (only if we haven't already)
                    var existing = hkcuUserShell.GetValue(regName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (existing != null && hkcuBackup.GetValue(regName) is null)
                    {
                        hkcuBackup.SetValue(regName, existing);
                        _logger.Information("Backed up original shell folder: {Name} = {Path}",
                            folderName, existing);
                    }

                    // Modern key (used by most apps)
                    hkcuUserShell.SetValue(regName, targetPath, RegistryValueKind.ExpandString);
                    // Legacy key (still consulted by Explorer in some paths)
                    hkcuShell.SetValue(regName, targetPath, RegistryValueKind.String);

                    // Authoritative: tell the shell about the new path via SHSetKnownFolderPath.
                    // This is what Windows itself uses when the user changes folder location
                    // from Properties; it fires the necessary shell notifications.
                    if (KnownFolderIds.TryGetValue(folderName, out var folderId))
                    {
                        try
                        {
                            var hr = SHSetKnownFolderPath(ref folderId, KF_FLAG_DEFAULT, IntPtr.Zero, targetPath);
                            if (hr == 0)
                                _logger.Debug("SHSetKnownFolderPath OK for {Name}", folderName);
                            else
                                _logger.Information("SHSetKnownFolderPath HRESULT 0x{Hr:X} for {Name} (registry was still updated)", hr, folderName);
                        }
                        catch (Exception ex)
                        {
                            _logger.Information(ex, "SHSetKnownFolderPath threw for {Name} (registry was still updated)", folderName);
                        }
                    }

                    hkcuApplied.SetValue(regName, targetPath);

                    applied.Add($"{folderName}->{targetPath}");
                    _logger.Information("Redirected {Name} -> {Target}", folderName, targetPath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Could not redirect {Name}", folderName);
                }
            }
        }

        if (applied.Count > 0)
        {
            NotifyShell();
            _logger.Information("Applied {Count} folder redirections: {List}",
                applied.Count, string.Join(", ", applied));

            // Explorer reads the known-folder registry once, at its own startup.
            // This app applies the redirect only after the drive mounts - well
            // after the user's Explorer is already running - so SHChangeNotify and
            // SHSetKnownFolderPath are not enough to make the running shell switch
            // to the new locations. Restarting Explorer is the one reliable way,
            // and it reloads the sync-state overlay handlers in the same step.
            RestartExplorer();
        }
    }

    public void Restore()
    {
        try
        {
            using var hkcuBackup = Registry.CurrentUser.OpenSubKey(BackupKey, writable: false);
            using var hkcuUserShell = Registry.CurrentUser.OpenSubKey(UserShellFoldersKey, writable: true);
            using var hkcuShell = Registry.CurrentUser.OpenSubKey(ShellFoldersKey, writable: true);
            using var hkcuApplied = Registry.CurrentUser.OpenSubKey(AppliedKey, writable: true);

            if (hkcuBackup is null || hkcuUserShell is null) return;

            int restored = 0;
            foreach (var name in hkcuBackup.GetValueNames())
            {
                var original = hkcuBackup.GetValue(name) as string;
                if (string.IsNullOrEmpty(original)) continue;

                try
                {
                    hkcuUserShell.SetValue(name, original, RegistryValueKind.ExpandString);
                    hkcuShell?.SetValue(name, Environment.ExpandEnvironmentVariables(original), RegistryValueKind.String);
                    hkcuApplied?.DeleteValue(name, throwOnMissingValue: false);
                    restored++;
                    _logger.Information("Restored shell folder: {Name} -> {Path}", name, original);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Could not restore shell folder {Name}", name);
                }
            }
            if (restored > 0)
            {
                NotifyShell();
                _logger.Information("Restored {Count} folder redirections", restored);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "FolderRedirector.Restore failed");
        }
    }

    private void NotifyShell()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Restarts the Windows shell (explorer.exe) so it re-reads the known-folder
    /// registry. Killing explorer.exe makes Windows relaunch it automatically
    /// (AutoRestartShell); we wait briefly and start it ourselves if it does not
    /// come back. Best-effort - a failure here just means the redirect shows
    /// after the next sign-in instead.
    /// </summary>
    private void RestartExplorer()
    {
        if (!Environment.UserInteractive)
        {
            _logger.Information(
                "Non-interactive session - skipping Explorer restart; redirections apply at next sign-in");
            return;
        }

        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); p.WaitForExit(3000); }
                catch { /* another session's shell, or already gone - ignore */ }
                finally { try { p.Dispose(); } catch { } }
            }

            // Winlogon normally relaunches the shell on its own. Give it a moment,
            // then start it ourselves if it did not come back.
            Thread.Sleep(1200);
            if (Process.GetProcessesByName("explorer").Length == 0)
            {
                var explorerPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(explorerPath);
            }

            _logger.Information("Restarted Explorer so it picks up the redirected shell folders");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Could not restart Explorer - redirections will take effect at the next sign-in");
        }
    }

    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // SHSetKnownFolderPath: tells Windows to relocate a known folder. This is the
    // proper API for runtime folder redirection - updates registry, refreshes
    // Quick Access pinned items, fires shell notifications.
    private const uint KF_FLAG_DEFAULT = 0x00000000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHSetKnownFolderPath(
        [In] ref Guid rfid,
        [In] uint dwFlags,
        [In] IntPtr hToken,
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath);
}
