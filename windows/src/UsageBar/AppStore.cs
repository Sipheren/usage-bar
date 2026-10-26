using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using UsageBar.Core;

namespace UsageBar;

internal sealed class AccountState
{
    public UsageSnapshot? Snapshot { get; set; }
    public AccountIssue? Issue { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? WaitUntil { get; set; }
    public string? Email { get; set; }
    public bool Fetching { get; set; }
}

/// <summary>Cost figures for one account: null until the first scan finishes ("Counting…").</summary>
internal sealed record CostState(CostSummary? Summary);

/// <summary>
/// The app's state: settings, each account's numbers and issue, cost summaries and exchange rates. Fetches follow
/// <see cref="FetchPolicy"/> with the last attempt saved to disk, so relaunches don't spend rate-limit budget
/// (decision 10). Everything raises <see cref="Changed"/> on the UI thread.
/// </summary>
internal sealed class AppStore
{
    public AppSettings Settings { get; private set; }
    public ExchangeRates Rates { get; private set; } = ExchangeRates.Fallback;
    public bool Demo { get; }
    public string? FixturesDir { get; }
    public Guid? SelectedTab { get; set; }
    public event Action? Changed;
    public DesktopSource Desktop { get; }

    readonly Dictionary<Guid, AccountState> states = new();
    readonly Dictionary<Guid, CostState> costs = new();
    readonly HttpClient http;
    readonly AccountFetcher fetcher;
    readonly CostService costService;
    readonly Dispatcher ui;
    readonly DispatcherTimer refreshTimer;
    readonly DispatcherTimer tickTimer;
    DateTimeOffset? lastAnyUpdate;

    static string SettingsPath => Path.Combine(Paths.AppData, "settings.json");
    static string StatePath => Path.Combine(Paths.AppData, "state.json");
    static string RatesPath => Path.Combine(Paths.AppData, "rates.json");

    public AppStore(bool demo, string? fixturesDir)
    {
        Demo = demo;
        FixturesDir = fixturesDir;
        ui = Dispatcher.CurrentDispatcher;
        http = new HttpClient(new SocketsHttpHandler { UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        Desktop = new DesktopSource(http);
        fetcher = new AccountFetcher(http, Desktop);
        costService = new CostService(Path.Combine(Paths.LocalAppData, "cost-cache.json"));
        Settings = demo ? DemoData.Settings() : LoadSettings();
        // Test data only: a Work account answered from the fixtures folder. Never saved (AppSettings.ToJson).
        if (fixturesDir is not null && !demo)
            Settings.Accounts.Add(Account.DefaultWork() with { Id = FixtureAccountId, Source = AccountSource.Fixture(fixturesDir) });
        if (demo) LoadDemo();
        else
        {
            LoadState();
            LoadRates();
        }
        refreshTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(Settings.RefreshMinutes) };
        refreshTimer.Tick += (_, _) => _ = RefreshAllAsync(FetchTrigger.Timer);
        tickTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(30) };
        tickTimer.Tick += (_, _) => Changed?.Invoke();
    }

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public void Start()
    {
        if (Demo) return;
        refreshTimer.Start();
        tickTimer.Start();
        _ = RefreshAllAsync(FetchTrigger.Launch);
    }

    public AccountState State(Account a) => states.TryGetValue(a.Id, out var s) ? s : states[a.Id] = new AccountState();

    public CostState? Cost(Account a) => costs.TryGetValue(a.Id, out var c) ? c : null;

    public DateTimeOffset? LastUpdated => states.Values.Select(s => s.LastSuccess).Where(d => d is not null).DefaultIfEmpty().Max() ?? lastAnyUpdate;

    static readonly Guid FixtureAccountId = Guid.Parse("00000000-0000-4000-8000-0000000000cc");

    public AccountPresentation Presentation(Account a)
    {
        var st = State(a);
        return new AccountPresentation(st.Issue, st.Snapshot is not null);
    }

    public bool IsStale(Account a) => State(a).LastSuccess is { } t && Now - t > TrayModel.StaleAfter;

    public string Money(double usd)
    {
        var code = Settings.Currency;
        return Currency.Convert(usd, code, Rates) is { } v ? Currency.Format(v, code) : Currency.Format(usd, "USD");
    }

    public string MoneyWhole(double usd)
    {
        var code = Settings.Currency;
        return Currency.Convert(usd, code, Rates) is { } v ? Currency.FormatWhole(v, code) : Currency.FormatWhole(usd, "USD");
    }

    // MARK: Refresh

    public async Task RefreshAllAsync(FetchTrigger trigger)
    {
        if (Demo) return;
        var tasks = Settings.Accounts.Select(a => RefreshAsync(a, trigger)).ToList();
        tasks.Add(RefreshCostsAsync());
        tasks.Add(RefreshRatesAsync());
        await Task.WhenAll(tasks);
    }

    async Task RefreshAsync(Account account, FetchTrigger trigger)
    {
        var st = State(account);
        if (st.Fetching) return;
        var source = account.Source;
        // Before Connect… the Claude app source reads only its config file and makes no request, so it always runs.
        var localOnly = source.Kind == SourceKind.ClaudeDesktop && !Settings.DesktopConnected;
        var interval = Settings.RefreshMinutes * 60.0;
        if (!localOnly && !FetchPolicy.ShouldFetch(trigger, st.LastAttempt, st.WaitUntil, interval, Now)) return;
        st.Fetching = true;
        if (!localOnly) st.LastAttempt = Now;
        Changed?.Invoke();
        try
        {
            var ctx = new DesktopContext(Settings.DesktopConnected, Settings.DesktopTokenChoice, st.Email);
            var report = await Task.Run(() => fetcher.FetchAsync(account, ctx, Now));
            if (report.Snapshot is not null)
            {
                st.Snapshot = report.Snapshot;
                st.LastSuccess = Now;
                st.WaitUntil = null;
            }
            st.Issue = report.Issue;
            if (report.Issue is { Kind: IssueKind.RateLimited, Until: { } until }) st.WaitUntil = until;
            if (report.Email is not null) st.Email = report.Email;
            if (report.DesktopChoice is { } choice) { Settings.DesktopTokenChoice[choice.AccountUuid] = choice.Path; SaveSettings(); }
        }
        finally
        {
            st.Fetching = false;
            SaveState();
            Changed?.Invoke();
        }
    }

    async Task RefreshCostsAsync()
    {
        foreach (var a in Settings.Accounts)
        {
            if (a.Source.Kind != SourceKind.ClaudeCode || a.LogsDir is null) { costs.Remove(a.Id); continue; }
            if (!costs.ContainsKey(a.Id)) costs[a.Id] = new CostState(null);
            var summary = await costService.SummaryAsync(Paths.Expand(a.LogsDir), Now);
            costs[a.Id] = new CostState(summary);
        }
        Changed?.Invoke();
    }

    async Task RefreshRatesAsync()
    {
        var code = Settings.Currency.ToUpperInvariant();
        if (!Currency.NeedsRate(code)) return;
        if (!Rates.IsFallback && !Rates.NeedsRefresh(Now) && Rates.Rates.ContainsKey(code)) return;
        try
        {
            var data = await http.GetByteArrayAsync(ExchangeRates.Url);
            if (ExchangeRates.Parse(data, Now) is { } r)
            {
                Rates = r;
                Write(RatesPath, JsonSerializer.Serialize(r, AppJson.Options));
                Log.Info($"rates: {r.Rates.Count} currencies");
                Changed?.Invoke();
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Info("rates: " + NetworkMessage.Short(e));
        }
    }

    // MARK: Settings changes

    public void UpdateSettings(Action<AppSettings> change)
    {
        var oldMinutes = Settings.RefreshMinutes;
        var oldCurrency = Settings.Currency;
        change(Settings);
        SaveSettings();
        if (oldMinutes != Settings.RefreshMinutes) refreshTimer.Interval = TimeSpan.FromMinutes(Settings.RefreshMinutes);
        if (oldCurrency != Settings.Currency) _ = RefreshRatesAsync();
        Changed?.Invoke();
    }

    /// <summary>Connect… for the Claude app source: from now on Usage Bar may read the app's sign-in.</summary>
    public void ConnectDesktop()
    {
        UpdateSettings(s => s.DesktopConnected = true);
        foreach (var a in Settings.Accounts.Where(a => a.Source.Kind == SourceKind.ClaudeDesktop))
            _ = RefreshAsync(a, FetchTrigger.Manual);
    }

    public void AccountSourceChanged(Account a)
    {
        states.Remove(a.Id);
        _ = RefreshAsync(Settings.Accounts.First(x => x.Id == a.Id), FetchTrigger.Manual);
        _ = RefreshCostsAsync();
    }

    // MARK: Persistence

    static AppSettings LoadSettings()
    {
        try { return File.Exists(SettingsPath) ? AppSettings.FromJson(File.ReadAllText(SettingsPath)) : new AppSettings(); }
        catch (IOException) { return new AppSettings(); }
    }

    public void SaveSettings()
    {
        if (!Demo) Write(SettingsPath, Settings.ToJson());
    }

    void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<Guid, PersistedAccountState>>(File.ReadAllText(StatePath), AppJson.Options);
            foreach (var (id, p) in saved ?? new())
                states[id] = new AccountState { Snapshot = p.Snapshot, LastSuccess = p.LastSuccess, LastAttempt = p.LastAttempt, WaitUntil = p.WaitUntil, Email = p.Email };
        }
        catch (Exception e) when (e is IOException or JsonException) { Log.Info("state: unreadable, starting fresh"); }
    }

    void SaveState()
    {
        if (Demo) return;
        var data = states.ToDictionary(kv => kv.Key, kv => new PersistedAccountState(kv.Value.Snapshot, kv.Value.LastSuccess,
                                                                                         kv.Value.LastAttempt, kv.Value.WaitUntil, kv.Value.Email));
        Write(StatePath, JsonSerializer.Serialize(data, AppJson.Options));
    }

    void LoadRates()
    {
        try
        {
            if (File.Exists(RatesPath) && JsonSerializer.Deserialize<ExchangeRates>(File.ReadAllText(RatesPath), AppJson.Options) is { Rates.Count: > 0 } r)
                Rates = r;
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    static void Write(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }
        catch (IOException e) { Log.Info("write failed: " + e.GetType().Name); }
    }

    // MARK: Demo

    void LoadDemo()
    {
        var now = Now;
        Rates = DemoData.Rates(now);
        var p = Settings.Accounts[0];
        var w = Settings.Accounts[1];
        states[p.Id] = new AccountState { Snapshot = DemoData.Personal(now), LastSuccess = now, Email = DemoData.PersonalEmail };
        states[w.Id] = new AccountState { Snapshot = DemoData.Work(now), LastSuccess = now, Email = DemoData.WorkOrganization };
        costs[p.Id] = new CostState(DemoData.PersonalCost(Rates.Rates["AUD"]));
        lastAnyUpdate = now;
    }
}
