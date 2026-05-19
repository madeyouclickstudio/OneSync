using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace OneSync.Widget;

internal enum SyncFileState { Syncing, Completed, Error }

internal sealed class SyncFileItem : INotifyPropertyChanged
{
    private string _fileName = string.Empty;
    private double _progress;
    private SyncFileState _state;
    private string? _errorMessage;

    public string FileName
    {
        get => _fileName;
        set { _fileName = value; OnPropertyChanged(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressFraction)); }
    }

    public double ProgressFraction => _progress / 100.0;

    public SyncFileState State
    {
        get => _state;
        set { _state = value; OnPropertyChanged(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    internal string DriveConfigId { get; set; } = string.Empty;
    internal string RelativePath { get; set; } = string.Empty;

    public ICommand? RetryCommand { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class ToastItem : INotifyPropertyChanged
{
    private string _message = string.Empty;
    private bool _isPersistent;

    public string Message
    {
        get => _message;
        set { _message = value; OnPropertyChanged(); }
    }

    public bool IsPersistent
    {
        get => _isPersistent;
        set { _isPersistent = value; OnPropertyChanged(); }
    }

    public ICommand? DismissCommand { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
