using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
    private string _headerIcon = "↻";
    private WidgetState _state = WidgetState.Starting;
    private string _accentHex = "#4CAF50";
    private bool _isThrottled;
    private string? _throttleMessage;
    private int _drivesLoadingCount;
    private bool _isVisible = true;
    private DispatcherTimer? _autoHideTimer;

    public SyncStatusViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        RetryAllCommand = new RelayCommand(OnRetryAll);
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
                    DriveStatuses[i] = $"{drive.Letter}: {drive.Label} — {count:N0} items";
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

    public void OnUploadStarted(DriveConfig drive, string relPath, long size)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var fileName = System.IO.Path.GetFileName(
                relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var existing = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (existing != null)
            {
                existing.Progress = 0;
                existing.State = SyncFileState.Syncing;
                return;
            }
            SyncingFiles.Add(new SyncFileItem
            {
                FileName = fileName,
                Progress = 0,
                State = SyncFileState.Syncing,
                DriveConfigId = drive.ConfigId,
                RelativePath = relPath,
            });
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

            var fileName = System.IO.Path.GetFileName(
                relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

            var existing = ErrorFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (existing != null)
            {
                existing.ErrorMessage = error;
                return;
            }

            var errorItem = new SyncFileItem
            {
                FileName = fileName,
                State = SyncFileState.Error,
                ErrorMessage = error,
                DriveConfigId = drive.ConfigId,
                RelativePath = relPath,
            };
            errorItem.RetryCommand = new RelayCommand(() => OnRetrySingle(errorItem));
            ErrorFiles.Add(errorItem);
            OnPropertyChanged(nameof(HasErrors));
            UpdateState();
        });
    }

    public void OnSyncOpStarted(DriveConfig drive, string relPath, string verb)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var fileName = System.IO.Path.GetFileName(
                relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var existing = SyncingFiles.FirstOrDefault(f =>
                f.DriveConfigId == drive.ConfigId && f.RelativePath == relPath);
            if (existing != null) return;
            SyncingFiles.Add(new SyncFileItem
            {
                FileName = $"{verb} {fileName}",
                Progress = 100,
                State = SyncFileState.Syncing,
                DriveConfigId = drive.ConfigId,
                RelativePath = relPath,
            });
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
            AddToast($"Conflict: {originalName} — saved as {conflictName}", persistent: false);
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
            _throttleMessage = $"Sync paused — resuming in {when}";
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
        toast.DismissCommand = new RelayCommand(() => Toasts.Remove(toast));
        Toasts.Add(toast);
        ShowWidget();

        if (!persistent)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Toasts.Remove(toast);
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
            var fileName = System.IO.Path.GetFileName(
                op.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (ErrorFiles.Any(f => f.DriveConfigId == op.DriveConfigId && f.RelativePath == op.RelativePath))
                continue;
            var errorItem = new SyncFileItem
            {
                FileName = fileName,
                State = SyncFileState.Error,
                ErrorMessage = op.ErrorMessage ?? "Upload failed",
                DriveConfigId = op.DriveConfigId,
                RelativePath = op.RelativePath,
            };
            errorItem.RetryCommand = new RelayCommand(() => OnRetrySingle(errorItem));
            ErrorFiles.Add(errorItem);
        }
        OnPropertyChanged(nameof(HasErrors));
    }

    internal void UpdateState()
    {
        if (_state == WidgetState.Starting && _drivesLoadingCount > 0)
        {
            HeaderIcon = "↻";
            HeaderText = "Loading your files…";
            AccentHex = "#4CAF50";
            ShowWidget();
            return;
        }

        bool hasSyncing = SyncingFiles.Count > 0;
        bool hasErrors = ErrorFiles.Count > 0;

        if (_isThrottled)
        {
            State = WidgetState.Syncing;
            HeaderIcon = "⏸";
            HeaderText = _throttleMessage ?? "Sync paused";
            AccentHex = "#FFA726";
            ShowWidget();
        }
        else if (hasSyncing && hasErrors)
        {
            State = WidgetState.Mixed;
            HeaderIcon = "↻";
            HeaderText = $"Syncing {SyncingFiles.Count} file{(SyncingFiles.Count == 1 ? "" : "s")}…";
            AccentHex = "#FFA726";
            ShowWidget();
        }
        else if (hasSyncing)
        {
            State = WidgetState.Syncing;
            HeaderIcon = "↻";
            HeaderText = $"Syncing {SyncingFiles.Count} file{(SyncingFiles.Count == 1 ? "" : "s")}…";
            AccentHex = "#FFA726";
            ShowWidget();
        }
        else if (hasErrors)
        {
            State = WidgetState.Error;
            HeaderIcon = "✕";
            HeaderText = $"Sync Errors ({ErrorFiles.Count})";
            AccentHex = "#EF5350";
            ShowWidget();
        }
        else
        {
            State = WidgetState.Idle;
            HeaderIcon = "✓";
            HeaderText = "All Synced";
            AccentHex = "#4CAF50";
            ScheduleAutoHide();
        }
    }

    private void ShowWidget()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer = null;
        IsVisible = true;
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
