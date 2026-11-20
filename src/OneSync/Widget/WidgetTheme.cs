using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Microsoft.Win32;

namespace OneSync.Widget;

// Light and dark palettes for the widgets. Follows the Windows app theme
// (Settings > Personalisation > Colours) and switches when it changes.
internal static class WidgetTheme
{
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["PrimaryText"] = "#FFFFFF",
        ["SecondaryText"] = "#C5FFFFFF",
        ["TertiaryText"] = "#8BFFFFFF",
        ["Stroke"] = "#24FFFFFF",
        ["Divider"] = "#15FFFFFF",
        ["SubtleFill"] = "#0FFFFFFF",
        ["SubtleFillPressed"] = "#0AFFFFFF",
        ["TrackBrush"] = "#2EFFFFFF",
        ["CriticalBrush"] = "#FF99A4",
        ["CriticalFill"] = "#1FFF99A4",
        ["SuccessBrush"] = "#6CCB5F",
        ["ScrollThumb"] = "#5CFFFFFF",
        ["HoverFill"] = "#22FFFFFF",
        ["HoverFillPressed"] = "#33FFFFFF",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["PrimaryText"] = "#E4000000",
        ["SecondaryText"] = "#9E000000",
        ["TertiaryText"] = "#72000000",
        ["Stroke"] = "#1A000000",
        ["Divider"] = "#0F000000",
        ["SubtleFill"] = "#09000000",
        ["SubtleFillPressed"] = "#06000000",
        ["TrackBrush"] = "#24000000",
        ["CriticalBrush"] = "#C42B1C",
        ["CriticalFill"] = "#14C42B1C",
        ["SuccessBrush"] = "#0F7B0F",
        ["ScrollThumb"] = "#48000000",
        ["HoverFill"] = "#0F000000",
        ["HoverFillPressed"] = "#18000000",
    };

    // The status colours the view model uses are the dark theme ones.
    // These are the same colours as Windows draws them on a light panel.
    private static readonly Dictionary<string, string> LightStatus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#6CCB5F"] = "#0F7B0F",
        ["#FFC83D"] = "#9D5D00",
        ["#FF99A4"] = "#C42B1C",
    };

    private const string DarkWindow = "#B3202020";
    private const string LightWindow = "#B3F3F3F3";

    public static bool IsLight
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
            }
            catch { return false; }
        }
    }

    // Applies the current theme now and again whenever Windows changes it.
    // onChanged runs after each switch, for anything the window colours itself.
    public static void Attach(Window window, SolidColorBrush windowBg, Action? onChanged = null)
    {
        void Apply()
        {
            bool light = IsLight;
            foreach (var (key, hex) in light ? Light : Dark)
                window.Resources[key] = new SolidColorBrush(Parse(hex));
            windowBg.Color = Parse(light ? LightWindow : DarkWindow);
            AcrylicHelper.Apply(new WindowInteropHelper(window).Handle, dark: !light);
            onChanged?.Invoke();
        }

        UserPreferenceChangedEventHandler handler = (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                window.Dispatcher.BeginInvoke(Apply);
        };
        SystemEvents.UserPreferenceChanged += handler;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= handler;

        Apply();
    }

    // A status or accent colour from the view model, adjusted for the theme.
    public static Color Accent(string darkHex)
    {
        if (!IsLight) return Parse(darkHex);
        if (LightStatus.TryGetValue(darkHex, out var lightHex)) return Parse(lightHex);

        // Anything else is the Windows accent, so use its darker shade.
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings()
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch { return Parse("#005FB8"); }
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
