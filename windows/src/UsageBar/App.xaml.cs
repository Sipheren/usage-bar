using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// Starts Usage Bar in the notification area. Options: <c>--demo</c> (built-in sample data, nothing read or saved),
/// <c>--fixtures &lt;dir&gt;</c> (the Work account answers from a fixtures folder), <c>--open flyout|settings|about|accounts|menu</c>
/// and <c>--theme light|dark</c>, plus in demo mode <c>--layout one</c>, <c>--style bars|percentages|icon</c> and
/// <c>--icons single</c> (for screenshots), <c>--write-icon &lt;path&gt;</c> (writes the app icon and exits).
/// </summary>
public partial class App : Application
{
    Mutex? single;
    EventWaitHandle? showSignal;
    AppStore? store;
    TrayController? tray;
    FlyoutWindow? flyout;
    MenuWindow? menu;
    SettingsWindow? settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        if (Arg("--write-icon") is { } iconPath)
        {
            IconRenderer.WriteIco(iconPath, new[] { 16, 20, 24, 32, 40, 48, 64, 256 });
            Shutdown();
            return;
        }

        if (Arg("--desktop-shape") is { } shapePath)
        {
            // Key names and value types of the Claude app's decrypted token cache, never values.
            try { File.WriteAllText(shapePath, new DesktopSource(new System.Net.Http.HttpClient()).ShapeDump()); }
            catch (Exception ex) { File.WriteAllText(shapePath, "failed: " + ex.GetType().Name); }
            Shutdown();
            return;
        }

        var demo = args.Contains("--demo");
        // One instance per user; a second launch asks the first to open its flyout.
        single = new Mutex(true, demo ? @"Local\UsageBar.Demo" : @"Local\UsageBar", out var first);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, demo ? @"Local\UsageBar.Demo.Show" : @"Local\UsageBar.Show");
        if (!first)
        {
            showSignal.Set();
            Shutdown();
            return;
        }

        if (!demo) StartLog();
        Log.Info($"start {BuildInfo.VersionLine}{(demo ? " (demo)" : "")}");
        Theme.ForceDark = Arg("--theme") switch { "dark" => true, "light" => false, _ => null };
        Theme.Apply();
        try { Resources["AppIconSource"] = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { }

        var fixtures = Arg("--fixtures") ?? Environment.GetEnvironmentVariable("USAGEBAR_FIXTURES_UI");
        store = new AppStore(demo, fixtures is { Length: > 0 } && Directory.Exists(fixtures) ? fixtures : null);
        if (demo)
        {
            // Screenshot switches for demo mode only; real settings are never touched.
            var s = store.Settings;
            if (Arg("--layout") == "one") s.DropdownLayout = DropdownLayout.OneAtATime;
            if (Arg("--style") is { } st) s.LabelStyle = st switch { "percentages" => LabelStyle.Percentages, "icon" => LabelStyle.IconOnly, _ => LabelStyle.Bars };
            if (Arg("--icons") == "single") s.TrayIcons = TrayIconMode.Single;
        }
        tray = new TrayController(store);
        flyout = new FlyoutWindow(store, OpenSettings, Quit);
        menu = new MenuWindow(store, OpenSettings, Quit);
        tray.LeftClick += (account, _) => ToggleFlyout(account);
        tray.RightClick += pt => menu.ShowAt(pt);
        store.Changed += () => tray.Update();
        tray.Update();
        store.Start();

        var signal = showSignal;
        ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => Dispatcher.BeginInvoke(() => ToggleFlyout(null)), null, Timeout.Infinite, false);

        switch (Arg("--open"))
        {
            case "flyout": Dispatcher.BeginInvoke(() => ToggleFlyout(null), System.Windows.Threading.DispatcherPriority.ApplicationIdle); break;
            case "menu":
                Dispatcher.BeginInvoke(() => { Native.GetCursorPos(out var p); menu.ShowAt(p); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                break;
            case { } tab: Dispatcher.BeginInvoke(() => OpenSettings(tab == "settings" ? null : tab), System.Windows.Threading.DispatcherPriority.ApplicationIdle); break;
        }
    }

    void ToggleFlyout(Guid? account)
    {
        if (flyout is null || tray is null) return;
        if (flyout.IsVisible || flyout.JustHidden)
        {
            if (flyout.IsVisible) flyout.Hide();
            return;
        }
        flyout.ShowAt(tray.IconRect(account), account);
    }

    void OpenSettings(string? tab)
    {
        if (store is null) return;
        if (settings is null || !settings.IsLoaded)
        {
            settings = new SettingsWindow(store);
            settings.Closed += (_, _) => settings = null;
            settings.Show();
        }
        settings.SelectTab(tab);
        if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
        settings.Activate();
    }

    void Quit()
    {
        tray?.Dispose();
        Log.Info("quit");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }

    static void StartLog()
    {
        var dir = Path.Combine(Paths.LocalAppData, "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "usagebar.log");
        // Keep the log small: start over once it passes 1 MB.
        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
        var gate = new object();
        Log.Sink = line =>
        {
            lock (gate)
            {
                try { File.AppendAllText(path, line + Environment.NewLine); }
                catch (IOException) { }
            }
        };
    }
}
