using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// Owns the notification-area icons: one per shown account (the default) or a single one. Redraws them when the data,
/// settings, theme or DPI change, and re-adds them when Explorer restarts. Left-click toggles the flyout; right-click
/// opens the menu.
/// </summary>
internal sealed class TrayController : IDisposable
{
    const int CallbackMessage = Native.WM_USER + 77;
    readonly AppStore store;
    readonly HwndSource window;
    readonly uint taskbarCreated;
    readonly Dictionary<uint, (IntPtr Icon, Guid? Account)> icons = new();

    public event Action<Guid?, Native.POINT>? LeftClick;
    public event Action<Native.POINT>? RightClick;

    public IntPtr Handle => window.Handle;

    public TrayController(AppStore store)
    {
        this.store = store;
        // A hidden top-level window (not message-only) so it also receives TaskbarCreated and WM_SETTINGCHANGE.
        window = new HwndSource(new HwndSourceParameters("UsageBarTray") { Width = 0, Height = 0, WindowStyle = 0 });
        window.AddHook(WndProc);
        taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Stable icon id per account, so Windows remembers where the user put each icon.</summary>
    /// <remarks>16 bits, because the click message carries the id in the high word of lParam.</remarks>
    static uint IconId(Guid account)
    {
        uint h = 2166136261;
        foreach (var b in account.ToByteArray()) h = (h ^ b) * 16777619;
        return (h & 0x7FFF) | 0x8000;
    }
    const uint SingleId = 1;

    /// <summary>
    /// The small-icon size at the primary monitor's current scale, where the notification area lives. Not the system
    /// DPI, which stays at its sign-in value when the scale changes; Update runs every tick, so a change shows within one.
    /// </summary>
    public static int IconSize
    {
        get
        {
            var primary = Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, 1);
            var dpi = Native.GetDpiForMonitor(primary, 0, out var x, out _) == 0 && x > 0 ? x : Native.GetDpiForSystem();
            return Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi);
        }
    }

    /// <summary>Brings the icons in line with the settings and the latest data.</summary>
    public void Update()
    {
        var plan = Plan(store, IconSize, Theme.TaskbarDark);
        foreach (var id in icons.Keys.Where(k => plan.All(p => p.Id != k)).ToList()) Remove(id);
        foreach (var p in plan) Set(p.Id, IconRenderer.ToHIcon(p.Image), p.Tip, p.Account);
    }

    /// <summary>
    /// What the tray should show: one icon per shown account (badge over bars or percentage), or a single icon with
    /// the first account's bars or percentage, or the spark. Settings uses the same plan for its preview.
    /// </summary>
    internal static List<TrayIconPlan> Plan(AppStore store, int n, bool dark)
    {
        var s = store.Settings;
        var shown = s.Accounts.Where(a => a.ShowInTray).ToList();
        var now = store.Now;
        var inputs = shown.Select(a => new TrayInput(a.Tag, a.Name, store.State(a).Snapshot, store.State(a).LastSuccess)).ToList();
        var model = TrayModel.Build(inputs, s.LabelStyle, s.Metrics, now);
        var fg = dark ? Colors.White : Colors.Black;
        var plan = new List<TrayIconPlan>();
        // Bars or numbers in each account's own colour tell the accounts apart (decision 17).
        void DrawFor(DrawingContext dc, IReadOnlyList<int> values, Color colour, bool missing)
        {
            if (s.LabelStyle == LabelStyle.Bars) IconRenderer.DrawBars(dc, n, missing ? Array.Empty<int>() : values, colour, dark);
            else IconRenderer.DrawNumbers(dc, n, missing ? Array.Empty<int>() : values, colour);
        }
        if (s.TrayIcons == TrayIconMode.PerAccount && s.LabelStyle != LabelStyle.IconOnly && shown.Count > 0)
        {
            for (var i = 0; i < shown.Count; i++)
            {
                var a = shown[i];
                var item = model.Accounts[i];
                var colour = Colour(a.Preset.For(dark), fg);
                plan.Add(new TrayIconPlan(IconId(a.Id), Draw(n, item.Stale, dc => DrawFor(dc, item.Values, colour, item.Missing)),
                                          TrayModel.AccountTooltip(a.Name, store.State(a).Snapshot, now), a.Id));
            }
            return plan;
        }
        var first = model.Accounts.FirstOrDefault();
        var firstAccount = shown.FirstOrDefault();
        var image = s.LabelStyle != LabelStyle.IconOnly && first is not null && firstAccount is not null
            ? Draw(n, first.Stale, dc => DrawFor(dc, first.Values, Colour(firstAccount.Preset.For(dark), fg), first.Missing))
            : Draw(n, false, dc => IconRenderer.DrawSpark(dc, n, fg));
        plan.Add(new TrayIconPlan(SingleId, image, model.Accounts.Count == 0 ? "Usage Bar" : model.SingleTooltip(), null));
        return plan;
    }

    static Color Colour(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return fallback; }
    }

    static BitmapSource Draw(int n, bool stale, Action<DrawingContext> draw) =>
        IconRenderer.Render(n, dc =>
        {
            // Stale data (over 15 minutes old) draws at half opacity, as on the Mac.
            if (stale) dc.PushOpacity(0.5);
            draw(dc);
            if (stale) dc.Pop();
        });

    void Set(uint id, IntPtr hIcon, string tip, Guid? account)
    {
        var data = Data(id);
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = hIcon;
        data.szTip = tip;
        var existed = icons.TryGetValue(id, out var old);
        if (!existed || !Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data))
        {
            // After TaskbarCreated an icon may still exist (a resent message, a fast restart): then modify it.
            if (!Native.Shell_NotifyIcon(Native.NIM_ADD, ref data)) Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
            data.uVersion = Native.NOTIFYICON_VERSION_4;
            Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
        }
        if (existed && old.Icon != IntPtr.Zero) Native.DestroyIcon(old.Icon);
        icons[id] = (hIcon, account);
    }

    void Remove(uint id)
    {
        var data = Data(id);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        if (icons.Remove(id, out var old) && old.Icon != IntPtr.Zero) Native.DestroyIcon(old.Icon);
    }

    Native.NOTIFYICONDATA Data(uint id) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATA>(), hWnd = window.Handle, uID = id,
        szTip = "", szInfo = "", szInfoTitle = "",
    };

    /// <summary>The icon's rectangle in physical screen pixels, for anchoring the flyout.</summary>
    public Native.RECT? IconRect(Guid? account)
    {
        var id = account is { } a && icons.ContainsKey(IconId(a)) ? IconId(a) : icons.Keys.FirstOrDefault();
        if (id == 0) return null;
        var ident = new Native.NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = window.Handle, uID = id };
        return Native.Shell_NotifyIconGetRect(ref ident, out var r) == 0 ? r : null;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var evt = Native.LoWord(lParam);
            var id = (uint)Native.HiWord(lParam) & 0xFFFF;
            var account = icons.TryGetValue(id, out var hit) ? hit.Account : null;
            var pt = new Native.POINT { X = Native.LoWord(wParam), Y = Native.HiWord(wParam) };
            switch (evt)
            {
                case Native.NIN_SELECT or Native.NIN_KEYSELECT:
                    LeftClick?.Invoke(account, pt);
                    break;
                case Native.WM_CONTEXTMENU:
                    RightClick?.Invoke(pt);
                    break;
            }
            handled = true;
        }
        else if (msg == (int)taskbarCreated)
        {
            // Explorer restarted: every icon must be added again.
            foreach (var (icon, _) in icons.Values) if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
            icons.Clear();
            Update();
        }
        else if (msg == Native.WM_SETTINGCHANGE && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Theme.Apply();
            Update();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in icons.Keys.ToList()) Remove(id);
        window.Dispose();
    }
}

internal sealed record TrayIconPlan(uint Id, BitmapSource Image, string Tip, Guid? Account);

internal static class TrayPreview
{
    public static IEnumerable<BitmapSource> Render(AppStore store) =>
        TrayController.Plan(store, TrayController.IconSize, Theme.TaskbarDark).Select(p => p.Image);
}
