using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// The flyout: the Windows counterpart of the Mac dropdown. Opens above the tray icon, closes when it loses focus,
/// and shows both accounts (layout A) or one at a time with the 30-day chart (layout B).
/// </summary>
public partial class FlyoutWindow : Window
{
    readonly AppStore store;
    readonly Action<string?> openSettings;
    readonly Action quit;
    DateTime hiddenAt = DateTime.MinValue;
    Native.RECT? lastIcon;

    internal FlyoutWindow(AppStore store, Action<string?> openSettings, Action quit)
    {
        this.store = store;
        this.openSettings = openSettings;
        this.quit = quit;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Theme.RemoveCaptionButtons(this);
            Theme.StyleWindow(this, Native.DWMSBT_TRANSIENTWINDOW);
            // With the acrylic backdrop behind us, a light tint keeps text contrast close to the preview.
            Root.SetResourceReference(Border.BackgroundProperty, "FlyoutTint");
        };
        Deactivated += (_, _) => HideFlyout();
        // Content grows when data arrives: keep the flyout anchored above the taskbar.
        SizeChanged += (_, _) => { if (IsVisible) Place(lastIcon); };
        store.Changed += () => { if (IsVisible) Rebuild(); };
        InputBindings.Add(new KeyBinding(new Relay(HideFlyout), Key.Escape, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new Relay(Refresh), Key.R, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Relay(() => OpenSettings(null)), Key.OemComma, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Relay(quit), Key.Q, ModifierKeys.Control));
    }

    /// <summary>True right after the flyout closed by losing focus: a click on the tray icon then means "close", not "reopen".</summary>
    public bool JustHidden => (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300;

    void Rebuild()
    {
        var vm = FlyoutVm.Build(store);
        DataContext = vm;
        LayoutA.Visibility = vm.LayoutA ? Visibility.Visible : Visibility.Collapsed;
        LayoutB.Visibility = vm.LayoutA ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void ShowAt(Native.RECT? iconRect, Guid? account)
    {
        if (account is not null) store.SelectedTab = account;
        lastIcon = iconRect;
        Rebuild();
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        Place(iconRect);
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        _ = store.RefreshAllAsync(FetchTrigger.MenuOpen);
    }

    void HideFlyout()
    {
        if (!IsVisible) return;
        hiddenAt = DateTime.UtcNow;
        Hide();
    }

    /// <summary>Above the icon on a bottom taskbar (or beside it on a side or top taskbar), 12 px from the work-area edge.</summary>
    void Place(Native.RECT? icon)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetCursorPos(out var cursor);
        var anchor = icon is { } r ? new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 } : cursor;
        var monitor = Native.MonitorFromPoint(anchor, 2);
        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref mi);
        Native.GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var scale = dpi / 96.0;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        var m = (int)Math.Round(12 * scale);
        var work = mi.rcWork;
        var full = mi.rcMonitor;
        int x = anchor.X - w / 2, y;
        if (work.Top > full.Top) y = work.Top + m;                                   // taskbar at the top
        else if (work.Left > full.Left) { x = work.Left + m; y = anchor.Y - h / 2; } // left
        else if (work.Right < full.Right) { x = work.Right - m - w; y = anchor.Y - h / 2; } // right
        else y = work.Bottom - m - h;                                                // bottom (the usual)
        x = Math.Clamp(x, work.Left + m, Math.Max(work.Left + m, work.Right - m - w));
        y = Math.Clamp(y, work.Top + m, Math.Max(work.Top + m, work.Bottom - m - h));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
    }

    void Refresh() => _ = store.RefreshAllAsync(FetchTrigger.Manual);

    void OpenSettings(string? tab)
    {
        HideFlyout();
        openSettings(tab);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    void About_Click(object sender, RoutedEventArgs e) => OpenSettings("about");
    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(null);
    void Quit_Click(object sender, RoutedEventArgs e) => quit();
    void Connect_Click(object sender, RoutedEventArgs e) => store.ConnectDesktop();

    void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            store.SelectedTab = id;
            Rebuild();
            UpdateLayout();
            Place(lastIcon);
        }
    }
}

internal sealed class Relay(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

/// <summary>Collapses an element whose bound value is null or an empty string.</summary>
internal sealed class NullToCollapsed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapsed when true, visible when false.</summary>
internal sealed class InverseVis : MarkupExtension, IValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
