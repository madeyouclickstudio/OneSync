using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using OneSync.Config;
using OneSync.Sync;

namespace OneSync.Widget;

internal enum WidgetState { Starting, Idle, Syncing, Error, Mixed }

internal sealed class SyncStatusViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private SyncQueue? _queue;
    private string _headerText = "Loading your files…";
    private string _headerIcon = "";
    private WidgetState _state = WidgetState.Starting;
    private string _accentHex = "#6CCB5F";
    private bool _isThrottled;
    private string? _throttleMessage;
    private int _drivesLoadingCount;
    private bool _isVisible = true;
    private DispatcherTimer? _autoHideTimer;
    private bool _dismissed;
    private readonly Dictionary<string, string> _letters = new();

    public SyncStatusViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        RetryAllCommand = new RelayCommand(OnRetryAll);
        HideCommand = new RelayCommand(Hide);
    }

    public System.Windows.Input.ICommand HideCommand { get; }

    // Hidden by hand: stay out of the way until something new happens.
    private void Hide()
    {
        _dismissed = true;
        _autoHideTimer?.Stop();
        _autoHideTimer = null;
        IsVisible = false;
    }

    public void SetDrives(IEnumerable<DriveConfig> drives)
    {
        foreach (var d in drives) Remember(d);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnPropertyChanged(); }
    }

    public void SetQueue(SyncQueue queue) => _queue = queue;

    public void NotifyReady()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_state == WidgetState.Starting && _drivesLoadingCount <= 0)
            {
                DriveStatuses.Clear();
                LoadExistingErrors();
                UpdateState();
            }
        });
    }

    public string HeaderText
    {
        get => _headerText;
        set { _headerText = value; OnPropertyChanged(); }
    }

    public string HeaderIcon
    {
        get => _headerIcon;
        set { _headerIcon = value; OnPropertyChanged(); }
    }

    public WidgetState State
    {
        get => _state;
        set { _state = value; OnPropertyChanged(); }
    }

    public string AccentHex
    {
        get => _accentHex;
        set { _accentHex = value; OnPropertyChanged(); }
    }

    public ObservableCollection<string> DriveStatuses { get; } = new();
    public ObservableCollection<SyncFileItem> SyncingFiles { get; } = new();
    public ObservableCollection<SyncFileItem> ErrorFiles { get; } = new();
    public ObservableCollection<ToastItem> Toasts { get; } = new();

    public RelayCommand RetryAllCommand { get; }

    public bool HasErrors => ErrorFiles.Count > 0;

    public void AddDriveStatus(string driveLetter, string label, bool mounted)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var text = mounted
                ? $"{driveLetter}: {label} mounted"
                : $"{driveLetter}: {label} mounting…";
            DriveStatuses.Add(text);
        });
    }

    public void AddMountWarning(string warning)
    {
        _dispatcher.BeginInvoke(() =>
        {
            AddToast(warning, persistent: true);
        });
    }

    public void OnInitialPollStarted(DriveConfig drive)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _drivesLoadingCount++;
        });
    }

    public void OnInitialPollProgress(DriveConfig drive, int count)
    {
        _dispatcher.BeginInvoke(() =>
        {
            for (int i = 0; i < DriveStatuses.Count; i++)
            {
                if (DriveStatuses[i].StartsWith($"{drive.Letter}:"))
                {
                    DriveStatuses[i] = $"{drive.Letter}: {drive.Label} - {count:N0} items";
                    break;
                }
            }
        });
    }

    public void OnInitialPollCompleted(DriveConfig drive)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _drivesLoadingCount--;
            for (int i = 0; i < DriveStatuses.Count; i++)
            {
                if (DriveStatuses[i].StartsWith($"{drive.Letter}:"))
                {
                    DriveStatuses[i] = $"{drive.Letter}: {drive.Label} ready";
                    break;
                }
            }

            if (_drivesLoadingCount <= 0)
            {
                DriveStatuses.Clear();
                LoadExistingErrors();
                UpdateState();
            }
        });
    }

    private SyncFileItem NewItem(string configId, string relPath, SyncFileState state,
        string? verb = null, string? error = null)
    {
        var rel = relPath.Replace('/', '\\').TrimStart('\\');
        var full = _letters.TryGetValue(configId, out var letter) ? $"{letter}:\\{rel}" : "";
        var (text, canRetry) = state == SyncFileState.Error
            ? SyncErrorText.Describe(error) : ("", true);
        var item = new SyncFileItem
        {
            FileName = System.IO.Path.GetFileName(rel),
            FullPath = full,
            Verb = verb,
            State = state,
            ErrorMessage = state == SyncFileState.Error ? text : null,
            ErrorDetail = error,
            CanRetry = canRetry,
            DriveConfigId = configId,
            RelativePath = relPath,
        };
        if (state == SyncFileState.Error)
            item.RetryCommand = new RelayCommand(() => OnRetrySingle(item));
        return item;
    }

    private void Remember(DriveConfig drive) => _letters[drive.ConfigId] = drive.Letter.TrimEnd(':');

    public void OnUploadStarted(DriveConfig drive, string relPath, long size)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Remember(drive);
            var existing = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (existing != null)
            {
                existing.Progress = 0;
                existing.State = SyncFileState.Syncing;
                return;
            }
            _dismissed = false;
            SyncingFiles.Add(NewItem(drive.ConfigId, relPath, SyncFileState.Syncing));
            UpdateState();
        });
    }

    public void OnUploadProgress(DriveConfig drive, string relPath, long sent, long total)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var item = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (item == null) return;
            item.Progress = total > 0 ? (sent * 100.0 / total) : 0;
        });
    }

    public void OnUploadCompleted(DriveConfig drive, string relPath, long size)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var item = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (item == null) return;

            item.State = SyncFileState.Completed;
            item.Progress = 100;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SyncingFiles.Remove(item);
                UpdateState();
            };
            timer.Start();
        });
    }

    public void OnUploadFailed(DriveConfig drive, string relPath, string error)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var syncing = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (syncing != null)
                SyncingFiles.Remove(syncing);

            Remember(drive);
            var existing = ErrorFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (existing != null) ErrorFiles.Remove(existing);

            _dismissed = false;
            ErrorFiles.Add(NewItem(drive.ConfigId, relPath, SyncFileState.Error, error: error));
            OnPropertyChanged(nameof(HasErrors));
            UpdateState();
        });
    }

    /// <summary>The uploader is done with this op for now. A row still showing as
    /// in progress means it failed, was skipped or was put back for a retry, so drop it.</summary>
    public void OnSyncOpEnded(DriveConfig drive, string relPath)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var item = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (item == null || item.State != SyncFileState.Syncing) return;
            SyncingFiles.Remove(item);
            UpdateState();
        });
    }

    public void OnSyncOpStarted(DriveConfig drive, string relPath, string verb)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Remember(drive);
            var existing = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (existing != null) return;
            _dismissed = false;
            SyncingFiles.Add(NewItem(drive.ConfigId, relPath, SyncFileState.Syncing, verb));
            UpdateState();
        });
    }

    public void OnSyncOpCompleted(DriveConfig drive, string relPath, string verb)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var item = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (item == null) return;
            item.State = SyncFileState.Completed;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SyncingFiles.Remove(item);
                UpdateState();
            };
            timer.Start();
        });
    }

    public void OnConflictDetected(ConflictInfo info)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var originalName = System.IO.Path.GetFileName(
                info.OriginalRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var conflictName = System.IO.Path.GetFileName(info.ConflictLocalPath);
            AddToast($"Conflict: {originalName} - saved as {conflictName}", persistent: false);
        });
    }

    public void OnSignificantThrottle(TimeSpan delay)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _isThrottled = true;
            string when = delay >= TimeSpan.FromMinutes(1)
                ? $"~{Math.Ceiling(delay.TotalMinutes):F0}m"
                : $"~{(int)delay.TotalSeconds}s";
            _throttleMessage = $"Sync paused - resuming in {when}";
            UpdateState();

            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _isThrottled = false;
                _throttleMessage = null;
                UpdateState();
            };
            timer.Start();
        });
    }

    public void OnHydrationDenied(DriveConfig drive, string relPath, string? serverMsg)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var fileName = System.IO.Path.GetFileName(
                relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            AddToast($"Access denied: {fileName}", persistent: false);
        });
    }

    public void OnOtherMachinePending(string machineName, int fileCount)
    {
        _dispatcher.BeginInvoke(() =>
        {
            AddToast($"Unsaved changes on {machineName} ({fileCount} file{(fileCount == 1 ? "" : "s")})", persistent: true);
        });
    }

    private void AddToast(string message, bool persistent)
    {
        var toast = new ToastItem
        {
            Message = message,
            IsPersistent = persistent,
        };
        toast.DismissCommand = new RelayCommand(() => { Toasts.Remove(toast); UpdateState(); });
        Toasts.Add(toast);
        _dismissed = false;
        ShowWidget();

        if (!persistent)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Toasts.Remove(toast);
                UpdateState();
            };
            timer.Start();
        }
    }

    private void OnRetrySingle(SyncFileItem item)
    {
        _queue?.RetryFailed(item.DriveConfigId, item.RelativePath);
        ErrorFiles.Remove(item);
        OnPropertyChanged(nameof(HasErrors));
        UpdateState();
    }

    private void OnRetryAll()
    {
        _queue?.RetryAllFailed();
        ErrorFiles.Clear();
        OnPropertyChanged(nameof(HasErrors));
        UpdateState();
    }

    private void LoadExistingErrors()
    {
        if (_queue == null) return;
        var failed = _queue.GetFailed(max: 50);
        foreach (var op in failed)
        {
            if (ErrorFiles.Any(f => f.DriveConfigId == op.DriveConfigId && f.RelativePath == op.RelativePath))
                continue;
            ErrorFiles.Add(NewItem(op.DriveConfigId, op.RelativePath, SyncFileState.Error,
                error: op.ErrorMessage ?? "Upload failed"));
        }
        OnPropertyChanged(nameof(HasErrors));
    }

    internal void UpdateState()
    {
        if (_state == WidgetState.Starting && _drivesLoadingCount > 0)
        {
            HeaderIcon = "";
            HeaderText = "Loading your files…";
            AccentHex = "#6CCB5F";
            ShowWidget();
            return;
        }

        bool hasSyncing = SyncingFiles.Count > 0;
        bool hasErrors = ErrorFiles.Count > 0;

        if (_isThrottled)
        {
            State = WidgetState.Syncing;
            HeaderIcon = "";
            HeaderText = _throttleMessage ?? "Sync paused";
            AccentHex = "#FFC83D";
            ShowWidget();
        }
        else if (hasSyncing && hasErrors)
        {
            State = WidgetState.Mixed;
            HeaderIcon = "";
            HeaderText = $"{SyncingText()} · {ErrorFiles.Count} couldn't sync";
            AccentHex = SystemAccent();
            ShowWidget();
        }
        else if (hasSyncing)
        {
            State = WidgetState.Syncing;
            HeaderIcon = "";
            HeaderText = SyncingText();
            AccentHex = SystemAccent();
            ShowWidget();
        }
        else if (hasErrors)
        {
            State = WidgetState.Error;
            HeaderIcon = "";
            HeaderText = $"{ErrorFiles.Count} file{(ErrorFiles.Count == 1 ? "" : "s")} couldn't sync";
            AccentHex = "#FF99A4";
            ShowWidget();
        }
        else
        {
            State = WidgetState.Idle;
            HeaderIcon = "";
            HeaderText = "All synced";
            AccentHex = "#6CCB5F";
            ScheduleAutoHide();
        }
    }

    // The rows on screen are only what's in flight; the queue knows what's still to come.
    private string SyncingText()
    {
        int n = SyncingFiles.Count(f => f.State == SyncFileState.Syncing);
        try { n += _queue?.CountPending() ?? 0; } catch { }
        n = Math.Max(n, 1);
        return $"Syncing {n} file{(n == 1 ? "" : "s")}…";
    }

    // Windows' accent colour, lightened for a dark panel, as the taskbar does.
    private static string SystemAccent()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch { return "#99EBFF"; }
    }

    private void ShowWidget()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer = null;
        if (_dismissed || SomeoneIsPresenting()) return;
        IsVisible = true;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    // Busy (full screen), D3D full screen or presentation mode: don't pop up over it.
    private static bool SomeoneIsPresenting()
    {
        try { return SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4; }
        catch { return false; }
    }

    private void ScheduleAutoHide()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            _autoHideTimer = null;
            IsVisible = false;
        };
        _autoHideTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
