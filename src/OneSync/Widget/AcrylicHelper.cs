using System;
using System.Runtime.InteropServices;

namespace OneSync.Widget;

internal static class AcrylicHelper
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // NOMOVE | NOSIZE | NOZORDER | NOACTIVATE | FRAMECHANGED
    private const uint SWP_REFRESH = 0x0002 | 0x0001 | 0x0004 | 0x0010 | 0x0020;

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    public static void Apply(IntPtr hwnd, bool dark = true)
    {
        if (!TryWin11Backdrop(hwnd, dark))
            TryWin10Acrylic(hwnd, dark);

        // The widgets never activate, so DWM won't redraw the backdrop after a
        // theme change on its own. Nudge the frame so it does.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_REFRESH);
    }

    private static bool TryWin11Backdrop(IntPtr hwnd, bool dark)
    {
        try
        {
            int darkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            int value = DWMSBT_TRANSIENTWINDOW;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryWin10Acrylic(IntPtr hwnd, bool dark)
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = dark ? 0xCC1A1A1Au : 0xCCF3F3F3u,
            };

            var data = new WindowCompositionAttribData
            {
                Attribute = WCA_ACCENT_POLICY,
                SizeOfData = Marshal.SizeOf<AccentPolicy>(),
            };

            var ptr = Marshal.AllocHGlobal(data.SizeOfData);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                data.Data = ptr;
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            // Fallback: solid background applied via XAML (#E61A1A1A)
        }
    }
}
