using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageBar.Core;

namespace UsageBar;

/// <summary>Settings: General, Accounts and About, as on the Mac.</summary>
public partial class SettingsWindow : Window
{
    readonly AppStore store;
    bool loading;

    internal SettingsWindow(AppStore store)
    {
        this.store = store;
        InitializeComponent();
        if (Application.Current.Resources["AppIconSource"] is ImageSource icon) Icon = icon;
        SourceInitialized += (_, _) => Theme.StyleWindow(this);
        store.Changed += OnStoreChanged;
        Closed += (_, _) => store.Changed -= OnStoreChanged;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        // After layout: SizeChanged comes before the window itself has been resized.
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(KeepOnScreen, System.Windows.Threading.DispatcherPriority.Background);
        LoadGeneral();
        LoadAccounts();
        LoadAbout();
        TabGeneral.IsChecked = true;
    }

    /// <summary>
    /// SizeToContent grows the window downwards when a taller tab opens (it is centred on the first one): move it up
    /// so its bottom stays inside the work area.
    /// </summary>
    void KeepOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        var mi = new Native.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(hwnd, 2), ref mi)) return;
        var up = Math.Min(r.Bottom - mi.rcWork.Bottom, r.Top - mi.rcWork.Top);
        if (up > 0) Native.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top - up, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    internal void SelectTab(string? tab)
    {
        (tab switch { "accounts" => TabAccounts, "about" => TabAbout, _ => TabGeneral }).IsChecked = true;
    }

    internal void ThemeChanged()
    {
        RefreshPreview();
        foreach (var vm in AccountsList.Items.OfType<AccountEditVm>()) vm.ThemeChanged();
    }

    void OnStoreChanged()
    {
        RateText.Text = Formatters.RateLine(store.Rates, store.Settings.Currency) ?? "";
        RefreshPreview();
        foreach (var vm in AccountsList.Items.OfType<AccountEditVm>()) vm.RefreshStatus();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        GeneralPage.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = TabAccounts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = TabAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
    }

    // MARK: General

    void LoadGeneral()
    {
        loading = true;
        var s = store.Settings;
        StyleBox.ItemsSource = LabelStyles.Offered.Select(x => x.Title()).ToList();
        StyleBox.SelectedIndex = Math.Max(0, Array.IndexOf(LabelStyles.Offered, s.LabelStyle == LabelStyle.Compact ? LabelStyle.Percentages : s.LabelStyle));
        IconsBox.ItemsSource = new[] { TrayIconMode.PerAccount.Title(), TrayIconMode.Single.Title() };
        IconsBox.SelectedIndex = s.TrayIcons == TrayIconMode.PerAccount ? 0 : 1;
        LayoutBox.ItemsSource = new[] { DropdownLayout.BothAccounts.Title(), DropdownLayout.OneAtATime.Title() };
        LayoutBox.SelectedIndex = s.DropdownLayout == DropdownLayout.BothAccounts ? 0 : 1;
        RefreshBox.ItemsSource = AppSettings.RefreshOptions.Select(m => $"{m} min").ToList();
        RefreshBox.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.RefreshOptions, s.RefreshMinutes));
        SessionSwitch.IsChecked = s.ShowSession;
        WeeklySwitch.IsChecked = s.ShowWeekly;
        ScopedSwitch.IsChecked = s.ShowScoped;
        LoginSwitch.IsChecked = StartupRegistry.IsEnabled;
        var region = TryRegion();
        var options = Currency.Options(region, store.Rates.Rates.Keys.ToHashSet());
        if (!options.Contains(s.Currency)) options = options.Append(s.Currency).ToArray();
        CurrencyBox.ItemsSource = options.Select(Currency.DisplayName).ToList();
        CurrencyBox.Tag = options;
        CurrencyBox.SelectedIndex = Array.IndexOf(options, s.Currency);
        RateText.Text = Formatters.RateLine(store.Rates, s.Currency) ?? "";
        UpdateRateRow();
        loading = false;
        RefreshPreview();
    }

    static string? TryRegion()
    {
        try { return new RegionInfo(CultureInfo.CurrentCulture.Name).ISOCurrencySymbol; }
        catch (ArgumentException) { return null; }
    }

    void UpdateRateRow()
    {
        var show = Currency.NeedsRate(store.Settings.Currency);
        RateRow.Visibility = RateSeparator.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        IconsRow.Visibility = store.Settings.LabelStyle == LabelStyle.IconOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    void General_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        store.UpdateSettings(s =>
        {
            s.LabelStyle = LabelStyles.Offered[Math.Max(0, StyleBox.SelectedIndex)];
            s.TrayIcons = IconsBox.SelectedIndex == 1 ? TrayIconMode.Single : TrayIconMode.PerAccount;
            s.DropdownLayout = LayoutBox.SelectedIndex == 1 ? DropdownLayout.OneAtATime : DropdownLayout.BothAccounts;
            s.RefreshMinutes = AppSettings.RefreshOptions[Math.Max(0, RefreshBox.SelectedIndex)];
            s.ShowSession = SessionSwitch.IsChecked == true;
            s.ShowWeekly = WeeklySwitch.IsChecked == true;
            s.ShowScoped = ScopedSwitch.IsChecked == true;
            if (CurrencyBox.Tag is string[] codes && CurrencyBox.SelectedIndex >= 0) s.Currency = codes[CurrencyBox.SelectedIndex];
        });
        RateText.Text = Formatters.RateLine(store.Rates, store.Settings.Currency) ?? "";
        UpdateRateRow();
    }

    void Login_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        StartupRegistry.Set(LoginSwitch.IsChecked == true);
        // Show what the registry really holds, in case the write was refused.
        loading = true;
        LoginSwitch.IsChecked = StartupRegistry.IsEnabled;
        loading = false;
    }

    /// <summary>The tray icons as they look now, at their real pixel size, on a taskbar-coloured chip.</summary>
    void RefreshPreview()
    {
        var dark = Theme.TaskbarDark;
        PreviewChip.Background = new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Color.FromRgb(0xEE, 0xEE, 0xEE));
        // One image pixel per screen pixel: size each image in DIPs so WPF's DPI scaling lands exactly on its pixels.
        var dpi = VisualTreeHelper.GetDpi(this);
        PreviewIcons.ItemsSource = TrayPreview.Render(store).Select(b => new PreviewIcon(b, b.PixelWidth / dpi.DpiScaleX)).ToList();
    }

    // MARK: Accounts

    void LoadAccounts()
    {
        AccountsList.ItemsSource = store.Settings.Accounts.Select(a => new AccountEditVm(store, a.Id)).ToList();
        var none = store.Settings.Accounts.Count == 0;
        NoAccountsCard.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        AddCard.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        // One account per sign-in source: a source already in use is greyed out (decision 16).
        AddClaudeCodeItem.IsEnabled = !store.HasAccount(SourceKind.ClaudeCode);
        AddDesktopItem.IsEnabled = !store.HasAccount(SourceKind.ClaudeDesktop);
        AddButton.IsEnabled = EmptyAddButton.IsEnabled = AccountSources.Addable.Any(k => !store.HasAccount(k));
    }

    void Connect_Click(object sender, RoutedEventArgs e) => store.ConnectDesktop();

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AccountEditVm vm) return;
        var name = store.Settings.Accounts.FirstOrDefault(a => a.Id == vm.Id)?.Name ?? "this account";
        if (MessageBox.Show(this, $"Remove {name}? Usage Bar stops showing it. Nothing is signed out.", "Remove Account",
                            MessageBoxButton.OKCancel, MessageBoxImage.None) != MessageBoxResult.OK) return;
        store.UpdateSettings(s => s.Accounts.RemoveAll(a => a.Id == vm.Id));
        LoadAccounts();
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        AddMenu.PlacementTarget = sender as UIElement ?? AddButton;
        AddMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        AddMenu.IsOpen = true;
    }

    void AddClaudeCode_Click(object sender, RoutedEventArgs e) => AddAccount(SourceKind.ClaudeCode);
    void AddDesktop_Click(object sender, RoutedEventArgs e) => AddAccount(SourceKind.ClaudeDesktop);

    /// <summary>The source's default account (Personal or Claude app), with another tag or colour only when another account has it.</summary>
    void AddAccount(SourceKind kind)
    {
        if (store.HasAccount(kind)) return;
        var others = store.Settings.Accounts;
        var account = Account.DefaultAccount(kind);
        var tags = others.Select(a => a.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tags.Contains(account.Tag))
            account = account with { Tag = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString()).First(t => !tags.Contains(t)) };
        if (others.Any(a => a.Colour == account.Colour)
            && AccountColours.All.FirstOrDefault(p => others.All(a => a.Colour != p.Id)) is { } free)
            account = account with { Colour = free.Id };
        store.UpdateSettings(s => s.Accounts.Add(account));
        store.AccountSourceChanged(account);
        LoadAccounts();
    }

    // MARK: About

    void LoadAbout()
    {
        AppIconImage.Source = IconRenderer.AppIcon(208);
        VersionText.Text = BuildInfo.VersionLine;
        BuiltText.Text = BuildInfo.Built;
        if (BuildInfo.ProjectFolder is { } folder)
            FolderText.Text = Paths.Abbreviate(folder);
        else
            FolderRow.Visibility = FolderSeparator.Visibility = Visibility.Collapsed;
    }

    void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (BuildInfo.ProjectFolder is { } f) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f}\"") { UseShellExecute = true });
    }
}

internal sealed record PresetItem(string Name, Brush Brush);

internal sealed record PreviewIcon(ImageSource Image, double Size);

/// <summary>One account's card in Settings > Accounts. Every edit is saved straight away, as on the Mac.</summary>
internal sealed class AccountEditVm : INotifyPropertyChanged
{
    readonly AppStore store;
    public Guid Id { get; }

    public AccountEditVm(AppStore store, Guid id)
    {
        this.store = store;
        Id = id;
    }

    Account A => store.Settings.Accounts.First(a => a.Id == Id);

    void Update(Func<Account, Account> change, [CallerMemberName] string? name = null)
    {
        store.UpdateSettings(s =>
        {
            var i = s.Accounts.FindIndex(a => a.Id == Id);
            if (i >= 0) s.Accounts[i] = change(s.Accounts[i]);
        });
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Badge)));
    }

    public string Name
    {
        get => A.Name;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) Revert(nameof(Name));
            else if (value != A.Name) Update(a => a with { Name = value.Trim() });
        }
    }

    public string Tag
    {
        get => A.Tag;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) Revert(nameof(Tag));
            else if (value != A.Tag) Update(a => a with { Tag = value.Trim().ToUpperInvariant() });
        }
    }

    /// <summary>The ten presets, each shown in the shade the tray and flyout use with the current theme.</summary>
    public List<PresetItem> Presets => AccountColours.All.Select(p => new PresetItem(p.Name, Brush(p.For(Theme.AppsDark)))).ToList();

    public int ColourIndex
    {
        get => AccountColours.All.ToList().FindIndex(p => p.Id == A.Preset.Id);
        set
        {
            if (value < 0 || value >= AccountColours.All.Count || AccountColours.All[value].Id == A.Colour) return;
            Update(a => a with { Colour = AccountColours.All[value].Id });
        }
    }

    public Brush Badge => Brush(A.Preset.Badge);

    public bool ShowInTray
    {
        get => A.ShowInTray;
        set { if (value != A.ShowInTray) Update(a => a with { ShowInTray = value }); }
    }

    List<AccountSource> Sources()
    {
        var list = new List<AccountSource> { AccountSource.ClaudeCode(A.Source.Kind == SourceKind.ClaudeCode ? A.Source.Dir : null), AccountSource.ClaudeDesktop };
        if (A.Source.Kind == SourceKind.Fixture) list.Add(A.Source);
        return list;
    }

    /// <summary>Each source, greyed out when another account already uses it (one account per source, decision 16).</summary>
    public List<SourceOption> SourceOptions =>
        Sources().Select(s => new SourceOption(s.Label, s.Kind == A.Source.Kind || !store.HasAccount(s.Kind))).ToList();

    string SourcesInUse => string.Join(",", SourceOptions.Select(o => o.Enabled));
    string? lastSourcesInUse;

    public int SourceIndex
    {
        get => Sources().FindIndex(s => s.Kind == A.Source.Kind);
        set
        {
            var sources = Sources();
            if (value < 0 || value >= sources.Count || sources[value].Kind == A.Source.Kind) return;
            var src = sources[value];
            if (src.Kind == SourceKind.Fixture) return;   // fixtures come from --fixtures, not the saved settings
            // One account per sign-in source (decision 16): a source another account uses can't be picked.
            if (store.HasAccount(src.Kind)) { Revert(nameof(SourceIndex)); return; }
            Update(a => a with
            {
                Source = src,
                LogsDir = src.Kind == SourceKind.ClaudeCode ? Path.Combine(src.Dir ?? Paths.DefaultClaudeConfigDir, "projects") : null,
            });
            store.AccountSourceChanged(A);
            RefreshStatus();
        }
    }

    public bool StatusOk => store.State(A) is { Snapshot: not null, Issue: null or { Kind: IssueKind.RateLimited } };
    public bool CanConnect => store.State(A).Issue?.Kind == IssueKind.NotConnected;

    public string Status
    {
        get
        {
            var st = store.State(A);
            if (StatusOk) return st.Email ?? "Signed in";
            if (st.Issue is { } issue) return issue.Kind == IssueKind.NotConnected ? "Not connected" : issue.Message();
            return st.Fetching ? "Checking…" : "Not checked yet";
        }
    }

    /// <summary>A rejected edit: tell the box to show the saved value again, once WPF has finished pushing this one.</summary>
    void Revert(string property) =>
        Application.Current.Dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property)));

    /// <summary>The swatches and badge are drawn in the current theme's shades.</summary>
    public void ThemeChanged()
    {
        foreach (var p in new[] { nameof(Presets), nameof(ColourIndex), nameof(Badge) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public void RefreshStatus()
    {
        foreach (var p in new[] { nameof(Status), nameof(StatusOk), nameof(CanConnect), nameof(SourceIndex) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        // Only when another account took or freed a source: rebuilding the list on every tick would close an open picker.
        var inUse = SourcesInUse;
        if (lastSourcesInUse is not null && inUse != lastSourcesInUse) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceOptions)));
        lastSourcesInUse = inUse;
    }

    /// <summary>
    /// Frozen on purpose: an unfrozen brush keeps a reference to every element that uses it, and with WPF's binding to
    /// a plain (non-notifying) object that chain kept each closed Settings window alive (found with --selftest).
    /// </summary>
    static Brush Brush(string hex)
    {
        try
        {
            var b = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch (FormatException) { return Brushes.Gray; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A sign-in source in an account's picker; shown as its label.</summary>
internal sealed record SourceOption(string Label, bool Enabled)
{
    public override string ToString() => Label;
}
