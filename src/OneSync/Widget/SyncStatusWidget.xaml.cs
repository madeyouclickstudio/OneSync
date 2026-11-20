using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            else if (e.PropertyName == nameof(SyncStatusViewModel.HeaderIcon))
                UpdateSpin();
            else if (e.PropertyName == nameof(SyncStatusViewModel.IsVisible))
                UpdateVisibility();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionBottomRight();
        ApplyWindowStyles();
        WidgetTheme.Attach(this, WindowBg, UpdateAccentColor);
        UpdateSpin();
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
            var color = WidgetTheme.Accent(_vm.AccentHex);
            HeaderIconText.Foreground = new SolidColorBrush(color);
            // Progress bars follow the header, so they're the accent while syncing.
            Resources["ProgressBrush"] = new SolidColorBrush(color);
        }
        catch { }
    }

    private const string SyncGlyph = "";
    private bool _spinning;

    // Turn the sync icon slowly while there's work going on. Skipped when the
    // user has turned animations off in Windows.
    private void UpdateSpin()
    {
        bool spin = _vm.HeaderIcon == SyncGlyph && SystemParameters.ClientAreaAnimation;
        if (spin == _spinning) return;
        _spinning = spin;

        if (spin)
        {
            var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            HeaderSpin.BeginAnimation(RotateTransform.AngleProperty, turn);
        }
        else
        {
            HeaderSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            HeaderSpin.Angle = 0;
        }
    }

    // New rows fade in, unless the user has turned animations off in Windows.
    private void FileRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        ((UIElement)sender).BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
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
