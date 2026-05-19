using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using OneSync.Sync;

namespace OneSync.Widget;

internal sealed class RecycleBinViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private RecycleBinService? _service;
    private string _headerText = "Recycle Bin";
    private bool _isLoading;
    private bool _isEmpty;

    public ObservableCollection<DeletedFileItem> Items { get; } = new();

    public string HeaderText
    {
        get => _headerText;
        set { _headerText = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        set { _isEmpty = value; OnPropertyChanged(); }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;

    public RecycleBinViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        RefreshCommand = new RelayCommand(Load);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public void SetService(RecycleBinService service) => _service = service;

    public void Load()
    {
        if (_service == null) return;

        IsLoading = true;
        IsEmpty = false;
        Items.Clear();

        var items = _service.GetRecentlyDeleted(50);
        foreach (var item in items)
        {
            var vm = new DeletedFileItem(item);
            vm.RestoreCommand = new RelayCommand(() => OnRestore(vm));
            Items.Add(vm);
        }

        IsLoading = false;
        IsEmpty = Items.Count == 0;
        UpdateHeader();
    }

    private async void OnRestore(DeletedFileItem item)
    {
        if (_service == null) return;
        item.IsRestoring = true;
        item.ErrorMessage = null;

        var (success, error) = await Task.Run(() => _service.RestoreAsync(item.Model));

        _dispatcher.BeginInvoke(() =>
        {
            if (success)
            {
                Items.Remove(item);
                IsEmpty = Items.Count == 0;
                UpdateHeader();
            }
            else
            {
                item.IsRestoring = false;
                item.ErrorMessage = error ?? "Restore failed";
            }
        });
    }

    private void UpdateHeader()
    {
        HeaderText = Items.Count > 0
            ? $"Recycle Bin ({Items.Count})"
            : "Recycle Bin";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class DeletedFileItem : INotifyPropertyChanged
{
    private bool _isRestoring;
    private string? _errorMessage;

    public DeletedItem Model { get; }

    public string FileName => Model.Name;
    public string Icon => Model.IsFolder ? "📁" : "📄";
    public string DriveLetter => Model.DriveLetter;
    public string SizeText => Model.IsFolder ? "" : " · " + FormatBytes(Model.Size);
    public string DeletedAgo => FormatTimeAgo(Model.DeletedAtUtc);

    public string FolderPath
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(
                Model.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return dir?.TrimStart('\\') ?? "";
        }
    }

    public bool IsRestoring
    {
        get => _isRestoring;
        set { _isRestoring = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowRestore)); }
    }

    public bool ShowRestore => !_isRestoring;

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public ICommand? RestoreCommand { get; set; }

    public DeletedFileItem(DeletedItem model) => Model = model;

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var u = 0;
        double v = bytes;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.#} {units[u]}";
    }

    private static string FormatTimeAgo(DateTime utc)
    {
        var elapsed = DateTime.UtcNow - utc;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays < 30) return $"{(int)elapsed.TotalDays}d ago";
        return utc.ToLocalTime().ToString("d MMM");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
