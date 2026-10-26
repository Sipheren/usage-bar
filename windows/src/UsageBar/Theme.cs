using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace UsageBar;

/// <summary>
/// Light and dark palettes for the flyout and Settings (following "app mode"), the taskbar colour for the tray icons
/// (following "Windows mode"), and the user's accent colour. Read from the registry; redrawn on WM_SETTINGCHANGE.
/// </summary>
internal static class Theme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Set from <c>--theme light|dark</c> for screenshots; null follows Windows.</summary>
    public static bool? ForceDark { get; set; }

    static bool ReadLight(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue(name) is int v ? v != 0 : true;
        }
        catch (System.Security.SecurityException) { return true; }
    }

    public static bool AppsDark => ForceDark ?? !ReadLight("AppsUseLightTheme");
    public static bool TaskbarDark => ForceDark ?? !ReadLight("SystemUsesLightTheme");

    /// <summary>The accent shade Windows uses for controls: Light 2 in dark mode, Dark 1 in light mode.</summary>
    public static Color Accent(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
            {
                var i = dark ? 1 : 4;
                return Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            }
        }
        catch (System.Security.SecurityException) { }
        return dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
    }

    static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    /// <summary>Puts the current palette into the application resources (every view uses DynamicResource).</summary>
    public static void Apply()
    {
        var dark = AppsDark;
        var r = Application.Current.Resources;
        var accent = Accent(dark);
        var accentBrush = new SolidColorBrush(accent);
        accentBrush.Freeze();
        r["Accent"] = accentBrush;
        r["IsDark"] = dark;
        if (dark)
        {
            r["Text"] = B(0xFFFFFFFF);
            r["Secondary"] = B(0x99FFFFFF);
            r["Track"] = B(0x24FFFFFF);
            r["Separator"] = B(0x1FFFFFFF);
            r["ChartBase"] = B(0x33FFFFFF);
            r["Tick"] = B(0xFFD9D9D9);
            r["OverPace"] = B(0xFFFDBA74);
            r["Hover"] = B(0x0FFFFFFF);
            r["Pressed"] = B(0x0AFFFFFF);
            r["ButtonFill"] = B(0x0FFFFFFF);
            r["FlyoutFill"] = B(0xF22C2C2C);
            r["MenuFill"] = B(0xFF2C2C2C);
            r["FlyoutTint"] = B(0xD92C2C2C);
            r["FlyoutBorder"] = B(0x14FFFFFF);
            r["WindowFill"] = B(0xFF202020);
            r["CardFill"] = B(0xFF2B2B2B);
            r["CardBorder"] = B(0x40000000);
            r["ControlFill"] = B(0x0FFFFFFF);
            r["ControlBorder"] = B(0x14FFFFFF);
            r["ControlBottom"] = B(0x80FFFFFF);
            r["SwitchOffBorder"] = B(0x99FFFFFF);
            r["SwitchOffKnob"] = B(0xCCFFFFFF);
            r["SwitchOnKnob"] = B(0xFF1A1A1A);
            r["TabSelectedFill"] = B(0x0FFFFFFF);
            r["TabText"] = B(0xBFFFFFFF);
            r["SegmentFill"] = B(0x12FFFFFF);
            r["SegmentSelected"] = B(0x29FFFFFF);
            r["Success"] = B(0xFF6CCB5F);
            r["ScrollThumb"] = B(0x66FFFFFF);
        }
        else
        {
            // Solid greys, not translucent black: over the acrylic backdrop DWM shows premultiplied black as clear.
            r["Text"] = B(0xFF1A1A1A);
            r["Secondary"] = B(0xFF616161);
            r["Track"] = B(0xFFD3D3D3);
            r["Separator"] = B(0xFFD8D8D8);
            r["ChartBase"] = B(0xFFC4C4C4);
            r["Tick"] = B(0xFF3A3A3C);
            r["OverPace"] = B(0xFF9A4407);
            r["Hover"] = B(0xFFE6E6E6);
            r["Pressed"] = B(0xFFEBEBEB);
            r["ButtonFill"] = B(0xFFE6E6E6);
            r["FlyoutFill"] = B(0xF5F3F3F3);
            r["MenuFill"] = B(0xFFF9F9F9);
            r["FlyoutTint"] = B(0xFFF3F3F3);   // opaque: dark text needs a solid surface over the backdrop
            r["FlyoutBorder"] = B(0x1A000000);
            r["WindowFill"] = B(0xFFF3F3F3);
            r["CardFill"] = B(0xFFFBFBFB);
            r["CardBorder"] = B(0x14000000);
            r["ControlFill"] = B(0xFFFFFFFF);
            r["ControlBorder"] = B(0x24000000);
            r["ControlBottom"] = B(0x73000000);
            r["SwitchOffBorder"] = B(0x9A000000);
            r["SwitchOffKnob"] = B(0xC2000000);
            r["SwitchOnKnob"] = B(0xFFFFFFFF);
            r["TabSelectedFill"] = B(0xB3FFFFFF);
            r["TabText"] = B(0xC2000000);
            r["SegmentFill"] = B(0xFFE3E3E3);
            r["SegmentSelected"] = B(0xFFFFFFFF);
            r["Success"] = B(0xFF0F7B0F);
            r["ScrollThumb"] = B(0xFF9E9E9E);
        }
        // Open windows keep the title bar and backdrop they were styled with: style them again.
        foreach (Window w in Application.Current.Windows)
            if (Styled.TryGetValue(w, out var backdrop)) StyleWindow(w, backdrop.Value);
    }

    /// <summary>Each styled window's backdrop, for restyling on a theme change; weak, so closed windows are not kept.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, StrongBox> Styled = new();

    sealed class StrongBox(int? value) { public int? Value { get; } = value; }

    /// <summary>Removes the system menu, so DWM draws no caption buttons on a borderless (WindowChrome) popup.</summary>
    public static void RemoveCaptionButtons(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        const int GWL_STYLE = -16;
        const long WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        var style = Native.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)));
    }

    /// <summary>Dark title bar and Win11 rounded corners, plus a system backdrop when asked.</summary>
    public static void StyleWindow(Window w, int? backdrop = null)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        Styled.AddOrUpdate(w, new StrongBox(backdrop));
        var dark = AppsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        if (backdrop is { } b)
        {
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
            var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref b, sizeof(int));
        }
    }
}
