using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using OneSync.Cleanup;
using OneSync.Config;
using OneSync.Sync;
using Serilog;

namespace OneSync.Shutdown;

/// <summary>
/// Coordinates graceful shutdown: catches OS shutdown/logoff signals, flushes
/// the sync queue with a timeout, then signals the main thread to exit.
/// </summary>
internal sealed class GracefulShutdown : IDisposable
{
    private readonly AppConfig _config;
    private readonly SyncEngine _sync;
    private readonly StorageCleanup? _cleanup;
    private readonly ILogger _logger;
    private readonly Action _requestExit;

    private SessionEndingEventHandler? _sessionEnding;
    private EventHandler? _processExit;
    private ConsoleCancelEventHandler? _cancelKey;
    private int _firing;
    private Form? _hiddenWindow;
    private IntPtr _hiddenWindowHandle;

    public GracefulShutdown(AppConfig config, SyncEngine sync, ILogger logger, Action requestExit,
        StorageCleanup? cleanup = null)
    {
        _config = config;
        _sync = sync;
        _cleanup = cleanup;
        _logger = logger;
        _requestExit = requestExit;
    }

    public void Register()
    {
        _sessionEnding = (s, e) => OnShutdown("SessionEnding/" + e.Reason);
        SystemEvents.SessionEnding += _sessionEnding;

        _processExit = (s, e) => OnShutdown("ProcessExit");
        AppDomain.CurrentDomain.ProcessExit += _processExit;

        _cancelKey = (s, e) => { e.Cancel = true; OnShutdown("CancelKeyPress"); };
        Console.CancelKeyPress += _cancelKey;

        // High priority so we get notified early during system shutdown
        try { SetProcessShutdownParameters(0x3FF, 0); } catch { /* best effort */ }

        // Create a hidden top-level window so we can call ShutdownBlockReasonCreate.
        // Without this, Windows kills us after ~20s during logoff; with it, Windows
        // keeps us alive for up to the user-configurable shutdown timeout.
        try
        {
            _hiddenWindow = new BlockerForm();
            // Force handle creation so we have a real HWND for the Win32 calls
            var _ = _hiddenWindow.Handle;
            _hiddenWindowHandle = _hiddenWindow.Handle;
            _logger.Debug("Shutdown blocker window created (HWND=0x{Hwnd:X})", _hiddenWindowHandle.ToInt64());
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not create shutdown blocker window - falling back to default 20s timeout");
        }

        _logger.Information("GracefulShutdown handlers registered");
    }

    private void TryBlockShutdown(string reason)
    {
        if (_hiddenWindowHandle == IntPtr.Zero) return;
        try
        {
            if (ShutdownBlockReasonCreate(_hiddenWindowHandle, reason))
                _logger.Information("Asked Windows to delay shutdown: \"{Reason}\"", reason);
        }
        catch (Exception ex) { _logger.Debug(ex, "ShutdownBlockReasonCreate failed"); }
    }

    private void TryUnblockShutdown()
    {
        if (_hiddenWindowHandle == IntPtr.Zero) return;
        try { ShutdownBlockReasonDestroy(_hiddenWindowHandle); }
        catch (Exception ex) { _logger.Debug(ex, "ShutdownBlockReasonDestroy failed"); }
    }

    /// <summary>
    /// A minimal hidden top-level window. Windows shutdown-blocking APIs require
    /// a real HWND - a NotifyIcon doesn't count.
    /// </summary>
    private sealed class BlockerForm : Form
    {
        public BlockerForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0;
            Size = new System.Drawing.Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-2000, -2000);
            WindowState = FormWindowState.Minimized;
            // Don't actually show the form - we just need the HWND
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_TOOLWINDOW (0x80) keeps it out of Alt+Tab
                cp.ExStyle |= 0x80;
                return cp;
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            // Stay invisible but ensure the handle is created
            if (!IsHandleCreated)
                base.SetVisibleCore(false);
            else
                base.SetVisibleCore(false);
        }
    }

    private void OnShutdown(string source)
    {
        if (Interlocked.Exchange(ref _firing, 1) != 0)
        {
            _logger.Information("Shutdown already in progress (received {Source})", source);
            return;
        }

        _logger.Information("Shutdown initiated by {Source}", source);

        // Mark clean shutdown IMMEDIATELY on receiving the signal. Windows kills
        // the process ~20s after logoff regardless of ShutdownBlockReason on
        // many configurations, and the flush + cleanup below can take that long
        // on its own. If we wait until those finish, the marker never gets
        // written and the next session falsely reports "unclean shutdown".
        try { _cleanup?.MarkCleanShutdown(); }
        catch (Exception ex) { _logger.Warning(ex, "Could not write clean_shutdown marker on signal"); }

        var pending = _sync.Queue.CountPending();
        if (pending > 0)
            TryBlockShutdown($"OneSync is uploading {pending} file{(pending == 1 ? "" : "s")}...");

        try
        {
            FlushSync().Wait(TimeSpan.FromSeconds(_config.SyncSettings.ShutdownTimeoutSeconds + 5));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Flush during shutdown threw");
        }

        // Compact sync_queue.db after the flush so completed-and-deleted rows
        // (which LiteDB leaves as fragmented free pages) get reclaimed. Pending
        // uploads still in the queue survive — this is defragmentation, not a
        // wipe. Write a marker file so the startup-side defensive sweep knows
        // when compaction last ran.
        try
        {
            _sync.Queue.Compact();
            var marker = System.IO.Path.Combine(
                OneSync.Util.PathUtil.Expand(@"%LOCALAPPDATA%\OneSync"),
                "sync_queue.last-compact");
            System.IO.File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            _logger.Information("sync_queue.db compacted on shutdown");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Compaction on shutdown failed (non-fatal)");
        }

        TryUnblockShutdown();
        try { _requestExit(); } catch { }
    }

    private async Task FlushSync()
    {
        var timeout = TimeSpan.FromSeconds(_config.SyncSettings.ShutdownTimeoutSeconds);
        var pending = _sync.Queue.CountPending();
        if (pending == 0)
        {
            _logger.Information("No pending sync operations - clean exit");
            return;
        }

        _logger.Information("Flushing {Pending} pending sync operations (timeout {Timeout})", pending, timeout);
        await _sync.FlushAndStopAsync(timeout);
    }

    public void Dispose()
    {
        if (_sessionEnding != null) SystemEvents.SessionEnding -= _sessionEnding;
        if (_processExit != null) AppDomain.CurrentDomain.ProcessExit -= _processExit;
        if (_cancelKey != null) Console.CancelKeyPress -= _cancelKey;
        TryUnblockShutdown();
        try { _hiddenWindow?.Dispose(); } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string pwszReason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
