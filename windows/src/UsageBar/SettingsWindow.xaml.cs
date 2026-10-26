using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
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
        LoadGeneral();
        LoadAccounts();
        LoadAbout();
        TabGeneral.IsChecked = true;
    }

    internal void SelectTab(string? tab)
    {
        (tab switch { "accounts" => TabAccounts, "about" => TabAbout, _ => TabGeneral }).IsChecked = true;
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
    }

    void Colour_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AccountEditVm vm) vm.PaletteOpen = true;
    }

    void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex } el && FindParentVm(el) is { } vm)
        {
            vm.ColorHex = hex;
            vm.PaletteOpen = false;
        }
    }

    static AccountEditVm? FindParentVm(DependencyObject d)
    {
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: AccountEditVm vm }) return vm;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return null;
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
        AddMenu.PlacementTarget = AddButton;
        AddMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        AddMenu.IsOpen = true;
    }

    void AddClaudeCode_Click(object sender, RoutedEventArgs e) => AddAccount(AccountSource.ClaudeCode());
    void AddDesktop_Click(object sender, RoutedEventArgs e) => AddAccount(AccountSource.ClaudeDesktop);

    void AddAccount(AccountSource source)
    {
        var used = store.Settings.Accounts.Select(a => a.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tag = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString()).First(t => !used.Contains(t));
        var account = new Account
        {
            Name = source.Kind == SourceKind.ClaudeCode ? "Claude Code" : "Claude app", Tag = tag,
            ColorHex = AccountEditVm.Swatches[store.Settings.Accounts.Count % AccountEditVm.Swatches.Length], Source = source,
            LogsDir = source.Kind == SourceKind.ClaudeCode ? Path.Combine(source.Dir ?? Paths.DefaultClaudeConfigDir, "projects") : null,
        };
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

    void Link_Navigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}

internal sealed record Swatch(string Hex, Brush Brush);

internal sealed record PreviewIcon(ImageSource Image, double Size);

/// <summary>One account's card in Settings > Accounts. Every edit is saved straight away, as on the Mac.</summary>
internal sealed class AccountEditVm : INotifyPropertyChanged
{
    public static readonly string[] Swatches = { "#D97757", "#3E7CC4", "#4C9A6A", "#8A6FD1", "#C9A227", "#D1607A", "#3BA3A3", "#7A7A7A" };

    readonly AppStore store;
    public Guid Id { get; }
    bool paletteOpen;

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
    }

    public string Name
    {
        get => A.Name;
        set { if (!string.IsNullOrWhiteSpace(value) && value != A.Name) Update(a => a with { Name = value.Trim() }); }
    }

    public string Tag
    {
        get => A.Tag;
        set { if (!string.IsNullOrWhiteSpace(value) && value != A.Tag) Update(a => a with { Tag = value.Trim().ToUpperInvariant() }); }
    }

    public string ColorHex
    {
        get => A.ColorHex;
        set { if (value != A.ColorHex) Update(a => a with { ColorHex = value }); }
    }

    public Brush Badge => Brush(A.BadgeHex);
    public Brush Color => Brush(A.ColorHex);
    public List<Swatch> Palette => Swatches.Select(h => new Swatch(h, Brush(h))).ToList();

    public bool PaletteOpen
    {
        get => paletteOpen;
        set { paletteOpen = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PaletteOpen))); }
    }

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

    public List<string> SourceOptions => Sources().Select(s => s.Label).ToList();

    public int SourceIndex
    {
        get => Sources().FindIndex(s => s.Kind == A.Source.Kind);
        set
        {
            var sources = Sources();
            if (value < 0 || value >= sources.Count || sources[value].Kind == A.Source.Kind) return;
            var src = sources[value];
            if (src.Kind == SourceKind.Fixture) return;   // fixtures come from --fixtures, not the saved settings
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

    public void RefreshStatus()
    {
        foreach (var p in new[] { nameof(Status), nameof(StatusOk), nameof(CanConnect), nameof(SourceIndex) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    static Brush Brush(string hex)
    {
        try { return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return Brushes.Gray; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
