using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace OneSync.Widget;

public partial class SyncStatusWidget : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly SyncStatusViewModel _vm;

    internal SyncStatusWidget(SyncStatusViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = _vm;
        InitializeComponent();

        Loaded += OnLoaded;
        SizeChanged += (_, _) => PositionBottomRight();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PositionBottomRight);

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SyncStatusViewModel.AccentHex))
                UpdateAccentColor();
            else if (e.PropertyName == nameof(SyncStatusViewModel.IsVisible))
                UpdateVisibility();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
        ApplyWindowStyles();
        AcrylicHelper.Apply(new WindowInteropHelper(this).Handle);
        UpdateAccentColor();
    }

    private void ApplyWindowStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 12;
        Top = workArea.Bottom - ActualHeight - 12;
    }

    private void UpdateAccentColor()
    {
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_vm.AccentHex);
            AccentBar.Background = new SolidColorBrush(color);
        }
        catch { }
    }

    private void UpdateVisibility()
    {
        // The ViewModel's IsVisible can flip after the window has been closed
        // during app shutdown (the dispatcher is still draining queued
        // PropertyChanged callbacks). Show() on a closed Window throws
        // InvalidOperationException - guard explicitly.
        if (!IsLoaded || _closed) return;

        if (_vm.IsVisible)
        {
            Show();
            PositionBottomRight();
        }
        else
        {
            Hide();
        }
    }

    private bool _closed;

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }
}
