using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// <c>--selftest &lt;report&gt;</c> (demo data only): hammers the tray icons, the flyout, the menu and Settings, three
/// times over, and records GDI and USER objects, handles and memory after each phase. A leak shows as growth that
/// repeats in later rounds; a one-off rise in the first round is WPF warming its caches.
/// </summary>
internal static class SelfTest
{
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);

    sealed record Sample(string Phase, uint Gdi, uint User, int Handles, double PrivateMb, double ManagedMb, int AliveSettings);

    static readonly List<WeakReference> SettingsWindows = new();

    static Sample Measure(string phase)
    {
        // Let WPF finish tearing down closed windows (it disposes their HWNDs on later dispatcher passes).
        for (var i = 0; i < 3; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return new Sample(phase, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1), p.HandleCount,
                          p.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(true) / 1048576.0, SettingsWindows.Count(w => w.IsAlive));
    }

    static async Task Pump(int ms = 5) => await Task.Delay(ms);

    public static async Task RunAsync(AppStore store, TrayController tray, FlyoutWindow flyout, MenuWindow menu,
                                      Func<string?, SettingsWindow?> openSettings, string reportPath)
    {
        var samples = new List<Sample>();
        await Pump(500);
        samples.Add(Measure("start"));
        var s = store.Settings;
        for (var round = 1; round <= 3; round++)
        {
            for (var i = 0; i < 300; i++)
            {
                s.LabelStyle = LabelStyles.Offered[i % 3];
                s.TrayIcons = i % 2 == 0 ? TrayIconMode.PerAccount : TrayIconMode.Single;
                tray.Update();
                if (i % 20 == 0) await Pump();
            }
            s.LabelStyle = LabelStyle.Bars;
            s.TrayIcons = TrayIconMode.PerAccount;
            tray.Update();
            samples.Add(Measure($"round {round}: 300 tray redraws"));

            for (var i = 0; i < 60; i++)
            {
                s.DropdownLayout = i % 2 == 0 ? DropdownLayout.BothAccounts : DropdownLayout.OneAtATime;
                flyout.ShowAt(tray.IconRect(null), null);
                await Pump(20);
                flyout.Hide();
                await Pump();
            }
            samples.Add(Measure($"round {round}: 60 flyout opens"));

            for (var i = 0; i < 40; i++)
            {
                Native.GetCursorPos(out var pt);
                menu.ShowAt(pt);
                await Pump(10);
                menu.Hide();
            }
            samples.Add(Measure($"round {round}: 40 menu opens"));

            for (var i = 0; i < 25; i++)
            {
                var w = openSettings(new[] { null, "accounts", "about" }[i % 3]);
                if (w is not null) SettingsWindows.Add(new WeakReference(w));
                await Pump(40);
                w?.Close();
                await Pump();
            }
            await Pump(500);
            samples.Add(Measure($"round {round}: 25 Settings windows"));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{"phase",-34} {"GDI",5} {"USER",5} {"handles",8} {"private MB",11} {"managed MB",11} {"live Settings",14}");
        foreach (var x in samples)
            sb.AppendLine($"{x.Phase,-34} {x.Gdi,5} {x.User,5} {x.Handles,8} {x.PrivateMb,11:0.0} {x.ManagedMb,11:0.0} {x.AliveSettings,14}");
        File.WriteAllText(reportPath, sb.ToString());
    }
}
