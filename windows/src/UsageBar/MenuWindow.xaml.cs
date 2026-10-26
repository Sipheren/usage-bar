using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using UsageBar.Core;

namespace UsageBar;

/// <summary>The tray icon's right-click menu: Refresh Now, Settings…, About Usage Bar, Quit Usage Bar.</summary>
public partial class MenuWindow : Window
{
    readonly AppStore store;
    readonly Action<string?> openSettings;
    readonly Action quit;

    internal MenuWindow(AppStore store, Action<string?> openSettings, Action quit)
    {
        this.store = store;
        this.openSettings = openSettings;
        this.quit = quit;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Theme.RemoveCaptionButtons(this);
            Theme.StyleWindow(this, Native.DWMSBT_TRANSIENTWINDOW);
            Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "FlyoutTint");
        };
        Deactivated += (_, _) => Hide();
        InputBindings.Add(new KeyBinding(new Relay(Hide), Key.Escape, ModifierKeys.None));
    }

    /// <summary>Opens with its bottom-left corner at the cursor, kept inside the work area.</summary>
    internal void ShowAt(Native.POINT cursor)
    {
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        var monitor = Native.MonitorFromPoint(cursor, 2);
        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref mi);
        Native.GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var scale = dpi / 96.0;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        var work = mi.rcWork;
        var x = Math.Clamp(cursor.X, work.Left, work.Right - w);
        var y = Math.Clamp(cursor.Y - h, work.Top, work.Bottom - h);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        Activate();
        Native.SetForegroundWindow(hwnd);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) { Hide(); _ = store.RefreshAllAsync(FetchTrigger.Manual); }
    void Settings_Click(object sender, RoutedEventArgs e) { Hide(); openSettings(null); }
    void About_Click(object sender, RoutedEventArgs e) { Hide(); openSettings("about"); }
    void Quit_Click(object sender, RoutedEventArgs e) => quit();
}
