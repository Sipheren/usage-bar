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
    /// <summary>The route the last fetch used ("token", "cookie" or "fixture"). A Claude app account on "token" follows the Claude Code login's rules.</summary>
    public string? LastRoute { get; set; }
    /// <summary>429 backoff level and the run of good reads since (decision 19).</summary>
    public FetchBackoff Backoff { get; set; } = new();
    /// <summary>Set when Refresh Now or Connect was skipped: the next check, shown in the account header. Not saved.</summary>
    public DateTimeOffset? SkippedUntil { get; set; }
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
    /// <summary>Accounts with a fetch running, and those to fetch again when it ends (their source changed meanwhile).</summary>
    readonly HashSet<Guid> inFlight = new(), fetchAgain = new();
    readonly Dictionary<Guid, CostState> costs = new();
    readonly HttpClient http;
    readonly AccountFetcher fetcher;
    readonly CostService costService;
    readonly Dispatcher ui;
    readonly DispatcherTimer pollTimer;
    readonly DispatcherTimer tickTimer;
    DateTimeOffset? lastLocalRefresh;

    static string SettingsPath => Path.Combine(Paths.AppData, "settings.json");
    static string UnreadableSettingsPath => Path.Combine(Paths.AppData, "settings.unreadable.json");
    static string StatePath => Path.Combine(Paths.AppData, "state.json");
    static string RatesPath => Path.Combine(Paths.AppData, "rates.json");

    public AppStore(bool demo, string? fixturesDir, string? scenario = null, int demoAccounts = 2)
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
        Settings = demo ? DemoData.Settings(demoAccounts) : LoadSettings();
        // Test data only: a Work account answered from the fixtures folder. Never saved (AppSettings.ToJson).
        if (fixturesDir is not null && !demo)
            Settings.Accounts.Add(Account.DefaultWork() with { Id = FixtureAccountId, Source = AccountSource.Fixture(fixturesDir) });
        if (demo && scenario == "edge") { Settings = DemoData.EdgeSettings(); LoadEdge(); }
        else if (demo) LoadDemo();
        else
        {
            LoadState();
            LoadRates();
        }
        // A fixed tick: each account fetches only once its own interval is due (FetchPolicy).
        pollTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = FetchPolicy.Tick };
        pollTimer.Tick += (_, _) => _ = RefreshAllAsync(FetchTrigger.Timer);
        tickTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(30) };
        tickTimer.Tick += (_, _) => Changed?.Invoke();
    }

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public void Start()
    {
        if (Demo) return;
        pollTimer.Start();
        tickTimer.Start();
        _ = RefreshAllAsync(FetchTrigger.Launch);
    }

    public AccountState State(Account a) => states.TryGetValue(a.Id, out var s) ? s : states[a.Id] = new AccountState();

    public CostState? Cost(Account a) => costs.TryGetValue(a.Id, out var c) ? c : null;

    static readonly Guid FixtureAccountId = Guid.Parse("00000000-0000-4000-8000-0000000000cc");

    public AccountPresentation Presentation(Account a)
    {
        var st = State(a);
        return new AccountPresentation(st.Issue, st.Snapshot is not null, st.SkippedUntil is { } next && next > Now ? next : null);
    }

    /// <summary>The setting (at least 15 minutes on the OAuth usage endpoint), doubled for each backoff level (decision 19).</summary>
    double BackgroundInterval(Account a, string? lastRoute, int backoffLevel = 0) =>
        FetchPolicy.BackgroundInterval(Settings.RefreshMinutes, FetchPolicy.IsTokenSource(a.Source.Kind, lastRoute), backoffLevel);

    /// <summary>The account's current interval, backoff included, for staleness.</summary>
    public double CurrentInterval(Account a)
    {
        var st = State(a);
        return BackgroundInterval(a, st.LastRoute, st.Backoff.Level);
    }

    /// <summary>Greyed only when the account has missed checks by its own schedule (<see cref="TrayModel.IsStale"/>).</summary>
    public bool IsStale(Account a)
    {
        var st = State(a);
        return TrayModel.IsStale(st.LastSuccess, CurrentInterval(a), st.WaitUntil, Now);
    }

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
        // The log scan and the rate check keep the refresh setting's pace; the 60 s tick only looks for due accounts.
        if (trigger != FetchTrigger.Timer || lastLocalRefresh is not { } last
            || (Now - last).TotalSeconds >= FetchPolicy.BackgroundInterval(Settings.RefreshMinutes, false) - FetchPolicy.TimerSlack)
        {
            lastLocalRefresh = Now;
            tasks.Add(RefreshCostsAsync());
            tasks.Add(RefreshRatesAsync());
        }
        await Task.WhenAll(tasks);
    }

    async Task RefreshAsync(Account account, FetchTrigger trigger)
    {
        // Demo mode reads and fetches nothing, including accounts added or switched in its Settings.
        if (Demo) return;
        // One request per account at a time. A manual refresh asked for meanwhile (a source change) runs after it.
        if (inFlight.Contains(account.Id))
        {
            if (trigger == FetchTrigger.Manual) fetchAgain.Add(account.Id);
            return;
        }
        var st = State(account);
        var source = account.Source;
        // Before Connect… the Claude app source reads only its config file and makes no request, so it always runs.
        var localOnly = source.Kind == SourceKind.ClaudeDesktop && !Settings.DesktopConnected;
        var interval = BackgroundInterval(account, st.LastRoute, st.Backoff.Level);
        var token = FetchPolicy.IsTokenSource(source.Kind, st.LastRoute);
        if (!localOnly && !FetchPolicy.ShouldFetch(trigger, st.LastAttempt, st.WaitUntil, interval, Now, token))
        {
            if (trigger == FetchTrigger.Launch) LogNotDue(account, st, interval);
            if (trigger == FetchTrigger.Manual)
            {
                // Say when the next check is, rather than ignoring the click (decision 19).
                var next = FetchPolicy.NextCheck(st.LastAttempt, st.WaitUntil, interval, Now);
                st.SkippedUntil = next;
                Log.Info($"fetch {source.Kind}: Refresh Now skipped, next check {Formatters.ClockTime(next)}");
                Changed?.Invoke();
            }
            return;
        }
        inFlight.Add(account.Id);
        st.SkippedUntil = null;
        st.Fetching = true;
        if (!localOnly) st.LastAttempt = Now;
        Changed?.Invoke();
        try
        {
            // A copy, because the fetch reads it on a worker thread while the UI thread may record a choice.
            var ctx = new DesktopContext(Settings.DesktopConnected, new Dictionary<string, string>(Settings.DesktopTokenChoice), st.Email);
            var report = await Task.Run(() => fetcher.FetchAsync(account, ctx, Now));
            if (report.Snapshot is not null)
            {
                st.Snapshot = report.Snapshot;
                st.LastSuccess = Now;
                st.WaitUntil = null;
            }
            st.Issue = report.Issue;
            if (report.Route is not null) st.LastRoute = report.Route;
            var levelBefore = st.Backoff.Level;
            // Backoff counts requests only: before Connect the Claude app source makes none.
            if (!localOnly)
            {
                if (report.Snapshot is not null) st.Backoff.Succeeded();
                else if (report.Issue is { Kind: IssueKind.RateLimited }) st.Backoff.RateLimited(BackgroundInterval(account, st.LastRoute));
                else st.Backoff.Failed();
            }
            var every = BackgroundInterval(account, st.LastRoute, st.Backoff.Level) / 60;
            if (report.Issue is { Kind: IssueKind.RateLimited, Until: { } until })
            {
                st.WaitUntil = until;
                var header = report.RetryAfter is { } ra ? $"{ra:0} s" : "none";
                Log.Info($"fetch {source.Kind}: rate limited (429, Retry-After {header}), waiting {FetchPolicy.RateLimitWait(report.RetryAfter):0} s " +
                         $"+ jitter {report.RateLimitJitter ?? 0:0} s, retrying at {Formatters.ClockTime(until)}; backoff level {st.Backoff.Level}, every {every:0} min");
            }
            else if (st.Backoff.Level < levelBefore)
                Log.Info($"fetch {source.Kind}: {FetchBackoff.SuccessesToStepDown} good reads, backoff level {st.Backoff.Level}, every {every:0} min");
            if (report.Email is not null) st.Email = report.Email;
            if (report.DesktopChoice is { } choice) { Settings.DesktopTokenChoice[choice.AccountUuid] = choice.Path; SaveSettings(); }
        }
        catch (Exception e)
        {
            // Anything the fetch didn't turn into an issue itself: say so on the account, log the type only.
            st.Issue = AccountIssue.Rejected("Couldn't check this account");
            if (!localOnly) st.Backoff.Failed();
            Log.Info($"fetch {source.Kind}: failed, {e.GetType().Name}");
        }
        finally
        {
            inFlight.Remove(account.Id);
            st.Fetching = false;
            SaveState();
            Changed?.Invoke();
            if (fetchAgain.Remove(account.Id) && Settings.Accounts.FirstOrDefault(a => a.Id == account.Id) is { } current)
                _ = RefreshAsync(current, FetchTrigger.Manual);
        }
    }

    /// <summary>Why a launch skipped an account: no request is spent, and the log says when the next one is due.</summary>
    void LogNotDue(Account account, AccountState st, double interval)
    {
        var kind = account.Source.Kind;
        if (st.WaitUntil is { } wait && wait > Now)
            Log.Info($"fetch {kind}: not fetched at launch, rate-limit wait until {Formatters.ClockTime(wait)}; backoff level {st.Backoff.Level}");
        else
            Log.Info($"fetch {kind}: not fetched at launch, next due {(st.LastAttempt is { } last ? Formatters.ClockTime(last.AddSeconds(interval)) : "now")} " +
                     $"(every {interval / 60:0} min); backoff level {st.Backoff.Level}");
    }

    /// <summary>Redraws everything bound to the store, for a change it doesn't see itself (the theme).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    async Task RefreshCostsAsync()
    {
        if (Demo) return;
        // A copy: the scan awaits, and Settings can add or remove accounts meanwhile.
        foreach (var a in Settings.Accounts.ToList())
        {
            if (a.Source.Kind != SourceKind.ClaudeCode || a.LogsDir is null) { costs.Remove(a.Id); continue; }
            if (!costs.ContainsKey(a.Id)) costs[a.Id] = new CostState(null);
            try
            {
                var summary = await costService.SummaryAsync(Paths.Expand(a.LogsDir), Now);
                costs[a.Id] = new CostState(summary);
            }
            catch (Exception e)
            {
                // One account's logs failing doesn't stop the others; it shows no cost rather than "Counting…".
                costs.Remove(a.Id);
                Log.Info("cost: failed, " + e.GetType().Name);
            }
        }
        Changed?.Invoke();
    }

    async Task RefreshRatesAsync()
    {
        if (Demo) return;
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
        var oldCurrency = Settings.Currency;
        change(Settings);
        SaveSettings();
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

    /// <summary>
    /// Saved settings as they are, or a first run (no file, or not JSON): one account per sign-in found, saved straight
    /// away so account ids stay stable (decision 16).
    /// </summary>
    static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath) && AppSettings.FromJson(File.ReadAllText(SettingsPath)) is { } saved) return saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Can't read it now: show what's signed in, but leave the file alone.
            Log.Info("settings: unreadable, " + e.GetType().Name);
            return AppSettings.FirstRun(SignInDetector.Detect());
        }
        // A file that isn't JSON at all: keep a copy before the fresh start overwrites it, as the Mac does.
        if (File.Exists(SettingsPath))
        {
            try
            {
                File.Copy(SettingsPath, UnreadableSettingsPath, true);
                Log.Info("settings: not readable, copied aside before a fresh start");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Don't overwrite what couldn't be copied.
                Log.Info("settings: not readable, and copying it aside failed: " + e.GetType().Name);
                return AppSettings.FirstRun(SignInDetector.Detect());
            }
        }
        var found = SignInDetector.Detect();
        var fresh = AppSettings.FirstRun(found);
        Log.Info($"first run: Claude Code login {(found.ClaudeCodeLogin ? "found" : "not found")}; adding " +
                 (fresh.Accounts.Count == 0 ? "no accounts" : string.Join(", ", fresh.Accounts.Select(a => a.Name))));
        Write(SettingsPath, fresh.ToJson());
        return fresh;
    }

    /// <summary>One account per sign-in source (decision 16).</summary>
    public bool HasAccount(SourceKind kind) => Settings.Accounts.Any(a => a.Source.Kind == kind);

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
            var upgraded = false;
            // Only accounts that still exist, so state for removed accounts is dropped on the next save.
            foreach (var (id, entry) in saved ?? new())
            {
                if (Settings.Accounts.FirstOrDefault(a => a.Id == id) is not { } account) continue;
                var p = entry;
                // A wait saved by 0.1.6 or earlier ends on the Retry-After boundary: add the jitter once (decision 19).
                if (p.UpgradeEarlierWait(BackgroundInterval(account, p.LastRoute), FetchPolicy.RateLimitJitter(), Now, out var newer))
                {
                    p = newer;
                    upgraded = true;
                    Log.Info($"fetch {account.Source.Kind}: wait saved by an earlier version, now until {Formatters.ClockTime(p.WaitUntil!.Value)} " +
                             $"with jitter; backoff level {p.Backoff?.Level ?? 0}");
                }
                states[id] = new AccountState
                {
                    Snapshot = p.Snapshot, LastSuccess = p.LastSuccess, LastAttempt = p.LastAttempt, WaitUntil = p.WaitUntil, Email = p.Email,
                    LastRoute = p.LastRoute, Backoff = p.Backoff is { } b ? b with { } : new FetchBackoff(),
                    // Still paused by a rate limit: show it again, as before the relaunch.
                    Issue = p.WaitUntil is { } until && until > Now ? AccountIssue.RateLimited(until) : null,
                };
            }
            if (upgraded) SaveState();
        }
        catch (Exception e) when (e is IOException or JsonException) { Log.Info("state: unreadable, starting fresh"); }
    }

    void SaveState()
    {
        if (Demo) return;
        var data = states.ToDictionary(kv => kv.Key, kv => new PersistedAccountState(kv.Value.Snapshot, kv.Value.LastSuccess,
                                                                                         kv.Value.LastAttempt, kv.Value.WaitUntil, kv.Value.Email,
                                                                                         kv.Value.Backoff with { }, kv.Value.LastRoute));
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Info("write failed: " + e.GetType().Name); }
    }

    // MARK: Demo

    void LoadEdge()
    {
        var now = Now;
        Rates = DemoData.Rates(now);
        var a = Settings.Accounts;
        states[a[0].Id] = new AccountState { Snapshot = DemoData.OverLimit(now), LastSuccess = now, Email = "a-very-long-address-for-layout-testing@example.com" };
        states[a[1].Id] = new AccountState { Snapshot = DemoData.Work(now), LastSuccess = now, Email = DemoData.WorkOrganization,
                                             Issue = AccountIssue.RateLimited(now.AddMinutes(5)) };
        states[a[2].Id] = new AccountState { Issue = AccountIssue.NotConnected };
        states[a[3].Id] = new AccountState { Snapshot = DemoData.Personal(now.AddMinutes(-20)), LastSuccess = now.AddMinutes(-20),
                                             Issue = AccountIssue.Network("offline") };
        states[a[4].Id] = new AccountState { Snapshot = DemoData.Unstarted(now), LastSuccess = now };
        states[a[5].Id] = new AccountState { Snapshot = DemoData.Work(now), LastSuccess = now };
        states[a[6].Id] = new AccountState { Snapshot = DemoData.Personal(now), LastSuccess = now };
        states[a[7].Id] = new AccountState { Snapshot = DemoData.Work(now), LastSuccess = now };
        var cost = DemoData.PersonalCost(Rates.Rates["AUD"]);
        cost.UnpricedModels = new List<string> { "example-model-1", "another-example-model" };
        costs[a[0].Id] = new CostState(cost);
    }

    void LoadDemo()
    {
        var now = Now;
        Rates = DemoData.Rates(now);
        var a = Settings.Accounts;
        if (a.Count > 0)
        {
            states[a[0].Id] = new AccountState { Snapshot = DemoData.Personal(now), LastSuccess = now, Email = DemoData.PersonalEmail };
            costs[a[0].Id] = new CostState(DemoData.PersonalCost(Rates.Rates["AUD"]));
        }
        if (a.Count > 1)
            states[a[1].Id] = new AccountState { Snapshot = DemoData.Work(now), LastSuccess = now, Email = DemoData.WorkOrganization };
    }
}
