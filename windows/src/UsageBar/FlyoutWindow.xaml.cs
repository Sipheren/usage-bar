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

    /// <summary>The account whose icon opened the flyout (null for the single icon or a second launch).</summary>
    public Guid? ShownFor { get; private set; }

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
        static Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Shown(vm.Content.View == DropdownView.Empty);
        LayoutA.Visibility = Shown(vm.Content.View == DropdownView.Stacked);
        LayoutB.Visibility = Shown(vm.Content.View == DropdownView.OneAtATime);
    }

    internal void ShowAt(Native.RECT? iconRect, Guid? account)
    {
        if (account is not null) store.SelectedTab = account;
        ShownFor = account;
        lastIcon = iconRect;
        Rebuild();
        Left = -10000;
        Top = -10000;
        MaxHeight = WorkAreaHeight(iconRect) - 24;
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

    /// <summary>The icon's monitor: anchor point, work area, monitor bounds and scale.</summary>
    static (Native.POINT Anchor, PixelRect Work, PixelRect Monitor, double Scale) Screen(Native.RECT? icon)
    {
        Native.GetCursorPos(out var cursor);
        var anchor = icon is { } r ? new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 } : cursor;
        var monitor = Native.MonitorFromPoint(anchor, 2);
        var mi = new Native.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref mi);
        Native.GetDpiForMonitor(monitor, 0, out var dpi, out _);
        return (anchor, Native.Pixels(mi.rcWork), Native.Pixels(mi.rcMonitor), dpi / 96.0);
    }

    /// <summary>Against the taskbar beside the icon (above it on the usual bottom taskbar), 12 px from it.</summary>
    void Place(Native.RECT? icon)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var (anchor, work, monitor, scale) = Screen(icon);
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        var (x, y) = FlyoutPlacement.Place(work, monitor, Native.TaskbarRect(), anchor.X, anchor.Y, w, h, (int)Math.Round(12 * scale));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
    }

    /// <summary>The usable height in DIPs on the icon's monitor, so many accounts scroll instead of running off screen.</summary>
    static double WorkAreaHeight(Native.RECT? icon)
    {
        var (_, work, monitor, scale) = Screen(icon);
        return FlyoutPlacement.Area(work, monitor, Native.TaskbarRect()).Area.Height / scale;
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
    void OpenAccounts_Click(object sender, RoutedEventArgs e) => OpenSettings("accounts");
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

