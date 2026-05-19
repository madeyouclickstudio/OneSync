using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DokanNet;
using OneSync.Config;
using Serilog;
using ILogger = Serilog.ILogger;
using IDokanLogger = DokanNet.Logging.ILogger;

namespace OneSync.FileSystem;

internal sealed class DriveManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly Dokan _dokanInstance;
    private readonly List<MountedDrive> _mounted = new();
    private readonly object _sync = new();
    private bool _disposed;

    public DriveManager(ILogger logger)
    {
        _logger = logger;
        _dokanInstance = new Dokan(new SerilogDokanLogger(logger));
    }

    public IReadOnlyList<MountedDrive> MountedDrives
    {
        get { lock (_sync) return _mounted.ToList(); }
    }

    public MountedDrive Mount(DriveConfig drive, QuotaCache quotaCache,
        Action<string, WatcherChangeTypes>? onLocalChange = null,
        IHydrationTrigger? hydration = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DriveManager));

        Directory.CreateDirectory(drive.LocalRootPath);

        var letter = drive.Letter.ToUpperInvariant();
        var mountPoint = $@"{letter}:\";

        if (IsDriveLetterInUse(letter))
        {
            throw new InvalidOperationException(
                $"Drive letter {letter}: is already in use by another process or filesystem. " +
                "Kill any other drive mappers (e.g., CDM) before mounting.");
        }

        var fs = new OneSyncDokanFS(drive, quotaCache, _logger, onLocalChange, hydration);

        var builder = new DokanInstanceBuilder(_dokanInstance)
            .ConfigureLogger(() => new SerilogDokanLogger(_logger))
            .ConfigureOptions(o =>
            {
                // MountManager is required for Explorer to pick up the drive in "This PC"
                o.Options = DokanOptions.FixedDrive | DokanOptions.MountManager;
                o.MountPoint = mountPoint;
                o.SingleThread = false;
                o.Version = (ushort)210;
            });

        var dokanInstance = builder.Build(fs);

        var info = new MountedDrive
        {
            Config = drive,
            FileSystem = fs,
            Instance = dokanInstance,
            MountPoint = mountPoint,
        };

        // Wait briefly for the mount to become ready
        var ready = WaitForMount(letter, TimeSpan.FromSeconds(15));
        info.Ready = ready;

        lock (_sync) _mounted.Add(info);

        if (ready)
        {
            _logger.Information("Mounted {Letter}: ({Label}) at {Mount} - local root {Local}",
                letter, drive.Label, mountPoint, drive.LocalRootPath);
            NotifyShellOfNewDrive(letter);
        }
        else
            _logger.Warning("Mount of {Letter}: did not become Ready within timeout - drive may still be initialising",
                letter);

        return info;
    }

    private void NotifyShellOfNewDrive(string letter)
    {
        try
        {
            // Tell Explorer a new drive arrived so This PC refreshes
            // SHCNE_DRIVEADD = 0x100, SHCNF_PATH = 0x0001
            var path = $@"{letter}:\";
            var pathBytes = System.Text.Encoding.Unicode.GetBytes(path + "\0");
            var handle = GCHandle.Alloc(pathBytes, GCHandleType.Pinned);
            try
            {
                SHChangeNotify(SHCNE_DRIVEADD, SHCNF_PATHW, handle.AddrOfPinnedObject(), IntPtr.Zero);
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                handle.Free();
            }
            _logger.Debug("Sent SHCNE_DRIVEADD notification for {Letter}:", letter);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not notify shell of new drive");
        }
    }

    private const uint SHCNE_DRIVEADD = 0x00000100;
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public bool IsDriveLetterInUse(string letter)
    {
        letter = letter.ToUpperInvariant();
        try
        {
            return DriveInfo.GetDrives().Any(d => d.Name.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private bool WaitForMount(string letter, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var info = new DriveInfo(letter);
                if (info.IsReady) return true;
            }
            catch
            {
                // not yet mounted
            }
            Thread.Sleep(200);
        }
        return false;
    }

    public void Unmount(MountedDrive drive)
    {
        try
        {
            _logger.Information("Unmounting {Letter}: ({Mount})", drive.Config.Letter, drive.MountPoint);
            // RemoveMountPoint signals Dokan to unmount; the instance Dispose closes the kernel link
            try { _dokanInstance.RemoveMountPoint(drive.MountPoint); } catch { }
            drive.Instance?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Unmount failed for {Letter}:", drive.Config.Letter);
        }
        finally
        {
            lock (_sync) _mounted.Remove(drive);
        }
    }

    public void UnmountAll()
    {
        List<MountedDrive> drives;
        lock (_sync) drives = _mounted.ToList();
        foreach (var d in drives) Unmount(d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnmountAll();
        try { _dokanInstance.Dispose(); } catch { }
    }
}

internal sealed class MountedDrive
{
    public required DriveConfig Config { get; init; }
    public required OneSyncDokanFS FileSystem { get; init; }
    public required DokanInstance Instance { get; init; }
    public required string MountPoint { get; init; }
    public bool Ready { get; set; }
}

internal sealed class SerilogDokanLogger : IDokanLogger
{
    private readonly ILogger _logger;
    public SerilogDokanLogger(ILogger logger) => _logger = logger.ForContext("Source", "Dokan");

    public bool DebugEnabled => false; // very chatty
    public void Debug(string message, params object[] args) { /* suppressed */ }
    public void Info(string message, params object[] args) => _logger.Information(message, args);
    public void Warn(string message, params object[] args) => _logger.Warning(message, args);
    public void Error(string message, params object[] args) => _logger.Error(message, args);
    public void Fatal(string message, params object[] args) => _logger.Fatal(message, args);
    public void Dispose() { }
}
