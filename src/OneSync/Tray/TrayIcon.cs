using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OneSync.Config;
using OneSync.Diagnostics;
using OneSync.FileSystem;
using OneSync.State;
using OneSync.Sync;
using Serilog;

namespace OneSync.Tray;

/// <summary>
/// WinForms NotifyIcon: drive list with quota, sync status, Sync Now action,
/// Open Logs, and Exit.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly QuotaCache _quotaCache;
    private readonly SyncEngine _sync;
    private readonly List<DriveConfig> _drives;
    private readonly string _logDirectory;
    private readonly ILogger _logger;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly Action _requestExit;
    private readonly Action? _toggleRecycleBin;
    private readonly PauseStateStore _pause;
    private readonly DiagnosticExporter _exporter;
    private readonly Icon _appIcon;
    private readonly HashSet<string> _drivesLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _initialProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();

    public TrayIcon(
        QuotaCache quotaCache,
        SyncEngine sync,
        IEnumerable<DriveConfig> drives,
        string logDirectory,
        Action requestExit,
        Action? toggleRecycleBin,
        PauseStateStore pause,
        DiagnosticExporter exporter,
        ILogger logger)
    {
        _quotaCache = quotaCache;
        _sync = sync;
        _drives = drives.ToList();
        _logDirectory = logDirectory;
        _logger = logger;
        _requestExit = requestExit;
        _toggleRecycleBin = toggleRecycleBin;
        _pause = pause;
        _exporter = exporter;

        _menu = new ContextMenuStrip();

        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
        _appIcon = System.IO.File.Exists(iconPath)
            ? new Icon(iconPath)
            : SystemIcons.Information;

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            Visible = true,
            Text = "OneSync",
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => OpenFirstDrive();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        // Wire delta-poller events for loading-state UI
        sync.DeltaPoller.InitialPollStarted += OnInitialPollStarted;
        sync.DeltaPoller.InitialPollProgress += OnInitialPollProgress;
        sync.DeltaPoller.InitialPollCompleted += OnInitialPollCompleted;

        // On-demand folder enumeration: show a one-shot balloon the first time the
        // user triggers Graph /children after launch (when session-cache mode is
        // wiping the cache each session, the first navigation post-login briefly
        // shows empty folders — this tells them it's normal, not broken).
        sync.Hydration.ActiveEnumerationsChanged += OnActiveEnumerationsChanged;

        // Upload progress events (wired after sync.Start() runs, since Uploader
        // is created there; we subscribe via a short loop)
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            for (int i = 0; i < 50 && sync.Uploader == null; i++)
                await System.Threading.Tasks.Task.Delay(100);
            if (sync.Uploader == null) return;
            sync.Uploader.UploadStarted += OnUploadStarted;
            sync.Uploader.UploadProgress += OnUploadProgress;
            sync.Uploader.UploadCompleted += OnUploadCompleted;
            sync.Uploader.UploadFailed += OnUploadFailed;
        });

        Refresh();
    }

    // Per-upload progress state (key: drive.ConfigId + relPath)
    private readonly Dictionary<string, (string FileName, long Total, long Sent, DateTime Started)> _activeUploads = new();
    private readonly object _uploadsLock = new();
    private static string MakeKey(DriveConfig d, string rel) => $"{d.ConfigId}|{rel}";

    private void OnUploadStarted(DriveConfig drive, string relPath, long size)
    {
        var key = MakeKey(drive, relPath);
        var fileName = System.IO.Path.GetFileName(relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        lock (_uploadsLock)
        {
            _activeUploads[key] = (fileName, size, 0L, DateTime.UtcNow);
        }
    }

    private void OnUploadProgress(DriveConfig drive, string relPath, long sent, long total)
    {
        var key = MakeKey(drive, relPath);
        lock (_uploadsLock)
        {
            if (_activeUploads.TryGetValue(key, out var prev))
                _activeUploads[key] = (prev.FileName, total, sent, prev.Started);
        }
        // Tooltip refresh picks up the new percentage on the next timer tick
    }

    private void OnUploadCompleted(DriveConfig drive, string relPath, long size)
    {
        var key = MakeKey(drive, relPath);
        lock (_uploadsLock) _activeUploads.Remove(key);
    }

    private void OnUploadFailed(DriveConfig drive, string relPath, string error)
    {
        var key = MakeKey(drive, relPath);
        lock (_uploadsLock) _activeUploads.Remove(key);
    }

    // First-session balloon: shown once when the first on-demand enumeration starts
    // after launch. Reassures users that brief blank-folder loading is expected
    // (session-cache mode wipes the placeholder tree at startup).
    private int _firstEnumerationBallooned;
    private void OnActiveEnumerationsChanged(int count)
    {
        if (count > 0 && Interlocked.Exchange(ref _firstEnumerationBallooned, 1) == 0)
        {
            try
            {
                _icon.ShowBalloonTip(6000, "OneSync",
                    "Loading your files from the cloud. This is normal at logon and " +
                    "only takes a moment per folder you open.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex) { _logger.Debug(ex, "First-enumeration balloon failed"); }
        }
        // Tooltip refresh picks up the new active-count on the next timer tick.
    }

    /// <summary>
    /// Reads the OneDrive pending-uploads manifest and, if any entries belong to
    /// machines OTHER than this one, balloons the user so they know they have
    /// unfinished work elsewhere. Runs async on a background task so it doesn't
    /// block tray startup.
    /// </summary>
    public void CheckOtherMachinePendingAsync()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // Small delay so the loading balloon shows first
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(12));

                var manifest = await _sync.Manifest.ReadAsync();
                if (manifest == null) return;

                var thisMachine = Environment.MachineName;
                var otherEntries = manifest.Entries
                    .Where(e => !string.Equals(e.Machine, thisMachine, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (otherEntries.Count == 0) return;

                // Group by machine
                var byMachine = otherEntries
                    .GroupBy(e => e.Machine)
                    .Select(g => new
                    {
                        Machine = g.Key,
                        Count = g.Count(),
                        Files = string.Join(", ", g.Take(3).Select(x =>
                            System.IO.Path.GetFileName(x.RelativePath.Replace('/', '\\')))),
                        Extra = Math.Max(0, g.Count() - 3),
                        Earliest = g.Min(x => x.QueuedAtUtc),
                    })
                    .ToList();

                var lines = new List<string>();
                foreach (var m in byMachine)
                {
                    var age = DateTime.UtcNow - m.Earliest;
                    string ago = age.TotalDays >= 1 ? $"{(int)age.TotalDays} day{(age.TotalDays >= 2 ? "s" : "")} ago"
                               : age.TotalHours >= 1 ? $"{(int)age.TotalHours} hour{(age.TotalHours >= 2 ? "s" : "")} ago"
                               : "just now";
                    var fileList = m.Extra > 0 ? $"{m.Files}, +{m.Extra} more" : m.Files;
                    lines.Add($"• {m.Machine}: {m.Count} file{(m.Count == 1 ? "" : "s")} ({fileList}) — last queued {ago}");
                }

                var body =
                    "You have unsaved changes on other computers:\n" +
                    string.Join("\n", lines) +
                    "\n\nSign in on those machines to finish uploading.";

                _icon.BalloonTipTitle = "Unsaved work on another machine";
                _icon.BalloonTipText = body.Length > 250 ? body.Substring(0, 247) + "..." : body;
                _icon.BalloonTipIcon = ToolTipIcon.Warning;
                _icon.ShowBalloonTip(20000);
                _logger.Information("Surfaced {Count} other-machine pending uploads from manifest", otherEntries.Count);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "CheckOtherMachinePendingAsync failed");
            }
        });
    }

    private void OnInitialPollStarted(DriveConfig drive)
    {
        lock (_stateLock) _drivesLoading.Add(drive.ConfigId);
        try { _icon.GetType(); /* touch the icon on UI thread next refresh */ } catch { }
    }

    private void OnInitialPollProgress(DriveConfig drive, int count)
    {
        lock (_stateLock) _initialProgress[drive.ConfigId] = count;
    }

    private void OnInitialPollCompleted(DriveConfig drive)
    {
        lock (_stateLock)
        {
            _drivesLoading.Remove(drive.ConfigId);
        }
    }

    public void Refresh()
    {
        try
        {
            BuildMenu();
            UpdateText();
            UpdateIcon();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Tray refresh failed");
        }
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = icon;
            _icon.ShowBalloonTip(5000);
        }
        catch { /* ignore */ }
    }

    private void BuildMenu()
    {
        _pause.ReloadFromRegistry();
        _menu.Items.Clear();

        // Header
        var header = new ToolStripLabel("OneSync")
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        // Drives
        foreach (var drive in _drives)
        {
            var quota = _quotaCache.GetCached(drive);
            var sizeText = quota.TotalBytes > 0
                ? $" — {FormatBytes(quota.RemainingBytes)} free"
                : "";
            var label = $"{drive.Letter}: {drive.Label}{sizeText}";
            var item = new ToolStripMenuItem(label, image: null,
                onClick: (_, _) => OpenDrive(drive));
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());

        // Sync status
        var pending = _sync.Queue.CountPending();
        var failed = _sync.Queue.CountFailed();
        string statusLine;
        if (failed > 0) statusLine = $"Sync errors: {failed}";
        else if (pending > 0) statusLine = $"Syncing: {pending} pending";
        else statusLine = "All synced";

        _menu.Items.Add(new ToolStripLabel(statusLine));

        // Pause / Resume submenu
        ToolStripMenuItem pauseRoot;
        if (_pause.IsPaused())
        {
            pauseRoot = new ToolStripMenuItem("Sync paused");
            var until = _pause.PausedUntilUtc();
            var resumeLabel = _pause.IsIndefinitelyPaused()
                ? "Resume now (currently indefinite)"
                : $"Resume now (auto-resumes at {until?.ToLocalTime():HH:mm})";
            pauseRoot.DropDownItems.Add(new ToolStripMenuItem(resumeLabel, image: null,
                (_, _) => _pause.Resume()));
        }
        else
        {
            pauseRoot = new ToolStripMenuItem("Pause sync");
            pauseRoot.DropDownItems.Add(new ToolStripMenuItem("Pause for 15 minutes", null,
                (_, _) => _pause.PauseUntil(DateTime.UtcNow.AddMinutes(15))));
            pauseRoot.DropDownItems.Add(new ToolStripMenuItem("Pause for 1 hour", null,
                (_, _) => _pause.PauseUntil(DateTime.UtcNow.AddHours(1))));
            pauseRoot.DropDownItems.Add(new ToolStripMenuItem("Pause until tomorrow 06:00", null,
                (_, _) => _pause.PauseUntil(NextTomorrowAt(6, 0))));
            pauseRoot.DropDownItems.Add(new ToolStripMenuItem("Pause indefinitely", null,
                (_, _) => _pause.PauseIndefinitely()));
        }
        _menu.Items.Add(pauseRoot);

        // Sync submenu — two groups:
        //   1. "Refresh from cloud" — fire an immediate delta poll so newly-added
        //      remote files appear in seconds instead of waiting for the next tick.
        //   2. "Force full resync" — wipe local delta state, re-fetch everything.
        var syncRoot = new ToolStripMenuItem("Sync");

        // Group 1: quick refresh (cheap — one delta call per drive)
        syncRoot.DropDownItems.Add(new ToolStripMenuItem(
            "Refresh all drives from cloud now",
            image: null,
            (_, _) => RefreshFromCloud(null)));
        foreach (var drive in _drives)
        {
            var driveCapture = drive;
            syncRoot.DropDownItems.Add(new ToolStripMenuItem(
                $"Refresh {drive.Letter}: ({drive.Label}) now",
                image: null,
                (_, _) => RefreshFromCloud(driveCapture)));
        }

        syncRoot.DropDownItems.Add(new ToolStripSeparator());

        // Group 2: force full resync (expensive — wipes delta token + metadata)
        foreach (var drive in _drives)
        {
            var driveCapture = drive;
            syncRoot.DropDownItems.Add(new ToolStripMenuItem(
                $"Force full resync of {drive.Letter}: ({drive.Label})",
                image: null,
                (_, _) => ForceFullResync(driveCapture)));
        }
        _menu.Items.Add(syncRoot);

        var exportItem = new ToolStripMenuItem("Export diagnostics…", image: null, (_, _) =>
        {
            try
            {
                _icon.ShowBalloonTip(2000, "OneSync", "Generating diagnostics…", ToolTipIcon.Info);
                var zip = _exporter.Export();
                _icon.ShowBalloonTip(8000, "OneSync",
                    $"Diagnostics saved:\n{zip}\n\nSend this zip to IT.",
                    ToolTipIcon.Info);
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zip}\""); } catch { /* best-effort */ }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Diagnostic export failed");
                MessageBox.Show($"Diagnostic export failed:\n{ex.Message}",
                    "OneSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
        _menu.Items.Add(exportItem);

        if (_toggleRecycleBin != null)
        {
            var recycleBin = new ToolStripMenuItem("Recycle Bin");
            recycleBin.Click += (_, _) => _toggleRecycleBin();
            _menu.Items.Add(recycleBin);
        }

        _menu.Items.Add(new ToolStripSeparator());

        var openLogs = new ToolStripMenuItem("Open Logs");
        openLogs.Click += (_, _) =>
        {
            try { Process.Start("explorer.exe", _logDirectory); }
            catch (Exception ex) { _logger.Debug(ex, "Open logs failed"); }
        };
        _menu.Items.Add(openLogs);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            _logger.Information("Exit clicked from tray");
            _requestExit();
        };
        _menu.Items.Add(exit);
    }

    private void UpdateText()
    {
        bool isLoading;
        int loadedSoFar;
        lock (_stateLock)
        {
            isLoading = _drivesLoading.Count > 0;
            loadedSoFar = _initialProgress.Values.Sum();
        }

        var pending = _sync.Queue.CountPending();
        var failed = _sync.Queue.CountFailed();
        string status;

        // Active-upload progress always wins for the tooltip - that's the most
        // pressing thing the user wants to know.
        string? activeLine = null;
        lock (_uploadsLock)
        {
            if (_activeUploads.Count > 0)
            {
                var first = _activeUploads.Values.First();
                var pct = first.Total > 0 ? (first.Sent * 100.0 / first.Total) : 0;
                activeLine = $"Uploading {first.FileName}: {pct:0}% ({FormatBytes(first.Sent)}/{FormatBytes(first.Total)})";
                if (_activeUploads.Count > 1)
                    activeLine += $" (+{_activeUploads.Count - 1} more)";
            }
        }

        // On-demand folder enumeration (triggered by Explorer FindFiles after the
        // session-cache wipe). Surface this above "All synced" so users see "we're
        // doing something" during the brief blank-folder window post-login.
        var activeEnums = _sync.Hydration.ActiveEnumerationCount;

        if (isLoading)
        {
            status = loadedSoFar > 0
                ? $"Loading files ({loadedSoFar:N0} so far)…"
                : "Loading files…";
        }
        else if (activeLine != null)
        {
            status = activeLine;
        }
        else if (activeEnums > 0)
        {
            status = activeEnums == 1
                ? "Loading folder from cloud…"
                : $"Loading {activeEnums} folders from cloud…";
        }
        else if (failed > 0) { status = $"{failed} sync errors"; }
        else if (pending > 0) { status = $"Syncing {pending}"; }
        else { status = "All synced"; }

        var totalFree = _drives.Sum(d => _quotaCache.GetCached(d).RemainingBytes);
        var tip = $"OneSync - {status}\n{_drives.Count} drives mounted, {FormatBytes(totalFree)} free total";
        if (_pause.IsPaused())
        {
            var until = _pause.PausedUntilUtc();
            var suffix = until.HasValue
                ? $"\nPAUSED until {until.Value.ToLocalTime():HH:mm}"
                : "\nPAUSED indefinitely";
            // NotifyIcon.Text has a 127-char limit; truncate the base if needed so suffix fits.
            const int Limit = 127;
            if (tip.Length + suffix.Length > Limit)
                tip = tip.Substring(0, Limit - suffix.Length);
            tip += suffix;
        }
        else if (tip.Length > 127)
        {
            tip = tip.Substring(0, 127);
        }
        _icon.Text = tip;
    }

    private void UpdateIcon()
    {
        _icon.Icon = _appIcon;
    }

    private void OpenFirstDrive()
    {
        var first = _drives.FirstOrDefault();
        if (first != null) OpenDrive(first);
    }

    /// <summary>Force an immediate delta poll. If <paramref name="drive"/> is null,
    /// polls every mounted drive in parallel. Used by the tray "Refresh from cloud"
    /// menu items so a user who knows a file just appeared remotely doesn't have to
    /// wait for the next scheduled tick.</summary>
    private void RefreshFromCloud(DriveConfig? drive)
    {
        var targets = drive is null ? _drives.ToArray() : new[] { drive };
        var label = drive is null ? "all drives" : $"{drive.Letter}: ({drive.Label})";
        _logger.Information("Refresh requested for {Target}", label);
        _icon.ShowBalloonTip(2000, "OneSync", $"Refreshing {label}…", ToolTipIcon.Info);

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            foreach (var d in targets)
            {
                try { await _sync.DeltaPoller.PollDriveAsync(d, System.Threading.CancellationToken.None); }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Refresh-from-cloud failed for {Letter}:", d.Letter);
                }
            }
        });
    }

    private void ForceFullResync(DriveConfig drive)
    {
        var confirm = MessageBox.Show(
            owner: null,
            text: $"Re-fetch all {drive.Letter}: metadata from the cloud?\n\n" +
                  "Local files are preserved — only the cloud-tracked metadata is wiped " +
                  "and re-built on the next delta poll (within ~5 minutes). " +
                  "Takes a few minutes for a large library.\n\n" +
                  "Continue?",
            caption: "OneSync — Force full resync",
            buttons: MessageBoxButtons.YesNo,
            icon: MessageBoxIcon.Warning,
            defaultButton: MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        _logger.Information("Force-resync requested for {Letter}: ({Label})", drive.Letter, drive.Label);
        try
        {
            _sync.Queue.ClearDeltaToken(drive.ConfigId);
            _sync.Metadata.ClearForDrive(drive.ConfigId);
            _sync.Hydration.ClearEnumerationStateForDrive(drive.ConfigId);
            _icon.ShowBalloonTip(5000, "OneSync",
                $"Re-syncing {drive.Letter}: — this may take a few minutes.",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Force-resync failed for {Letter}:", drive.Letter);
            MessageBox.Show($"Force-resync of {drive.Letter}: failed: {ex.Message}",
                "OneSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenDrive(DriveConfig drive)
    {
        try
        {
            Process.Start("explorer.exe", $"{drive.Letter}:\\");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Open drive failed");
        }
    }

    private static DateTime NextTomorrowAt(int hour, int minute)
    {
        var tomorrow = DateTime.Now.Date.AddDays(1);
        var target = new DateTime(tomorrow.Year, tomorrow.Month, tomorrow.Day, hour, minute, 0, DateTimeKind.Local);
        return target.ToUniversalTime();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        var u = 0;
        double v = bytes;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    public void Dispose()
    {
        try { _refreshTimer.Stop(); _refreshTimer.Dispose(); } catch { }
        try { _icon.Visible = false; _icon.Dispose(); } catch { }
        try { _menu.Dispose(); } catch { }
    }
}
