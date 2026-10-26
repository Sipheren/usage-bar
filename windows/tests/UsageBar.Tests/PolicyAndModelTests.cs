using System.Text.Json;
using UsageBar.Core;

namespace UsageBar.Tests;

public class FetchPolicyTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T06:30:00Z");
    const double Interval = 120;

    static bool Should(FetchTrigger t, double? last, double? waitFor = null) =>
        FetchPolicy.ShouldFetch(t, last is { } l ? Now.AddSeconds(-l) : null,
                                waitFor is { } w ? Now.AddSeconds(w) : null, Interval, Now);

    [Fact]
    public void LaunchSkipsAnAccountFetchedWithinTheInterval()
    {
        Assert.False(Should(FetchTrigger.Launch, 30));
        Assert.True(Should(FetchTrigger.Launch, 121));
        Assert.True(Should(FetchTrigger.Launch, null));
    }

    [Fact]
    public void MenuOpenUsesTheRefreshInterval()
    {
        Assert.False(Should(FetchTrigger.MenuOpen, 90));
        Assert.True(Should(FetchTrigger.MenuOpen, 120));
    }

    [Fact]
    public void ManualRefreshForcesUnlessInsideRetryAfter()
    {
        Assert.True(Should(FetchTrigger.Manual, 5));
        Assert.False(Should(FetchTrigger.Manual, 5, 60));
    }

    [Fact]
    public void NothingFetchesInsideARetryAfterWindow()
    {
        foreach (var t in Enum.GetValues<FetchTrigger>()) Assert.False(Should(t, 999, 1));
        Assert.True(Should(FetchTrigger.Timer, 999, -1));   // window passed
    }
}

public class RateLimitPresentationTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly DateTimeOffset Until = DateTimeOffset.Parse("2026-10-26T06:40:00Z");

    [Fact]
    public void RateLimitWithDataKeepsNumbersAndNotesItInTheHeader()
    {
        var p = new AccountPresentation(AccountIssue.RateLimited(Until), true, Utc);
        Assert.Equal("Paused by rate limit · retrying 6:40 am", p.HeaderNote);
        Assert.Null(p.InlineIssue);
        Assert.False(p.GreysValues);
    }

    [Fact]
    public void RateLimitWithoutDataIsShownAsAnError()
    {
        var p = new AccountPresentation(AccountIssue.RateLimited(Until), false, Utc);
        Assert.Null(p.HeaderNote);
        Assert.Equal(AccountIssue.RateLimited(Until), p.InlineIssue);
        Assert.Equal("Rate limited — retrying after 6:40 am", AccountIssue.RateLimited(Until).Message(Utc));
    }

    [Fact]
    public void OtherIssuesStillShowInlineAndGreyOldNumbers()
    {
        var p = new AccountPresentation(AccountIssue.Network("offline"), true, Utc);
        Assert.Null(p.HeaderNote);
        Assert.Equal(AccountIssue.Network("offline"), p.InlineIssue);
        Assert.True(p.GreysValues);
    }

    [Fact]
    public void NoIssueShowsNothing()
    {
        var p = new AccountPresentation(null, true, Utc);
        Assert.Null(p.HeaderNote);
        Assert.Null(p.InlineIssue);
        Assert.False(p.GreysValues);
    }

    [Fact]
    public void IssueMessages()
    {
        Assert.Equal("Claude Code login expired — run `claude` once", AccountIssue.ExpiredCli.Message(Utc));
        Assert.Equal("This sign-in can't read usage (scope)", AccountIssue.Scope.Message(Utc));
        Assert.Equal("Blocked by a Cloudflare check — open claude.ai in Firefox", AccountIssue.Cloudflare("Firefox").Message(Utc));
        Assert.Equal("The Claude app is signed in to your Personal account", AccountIssue.SignedInToPersonal("The Claude app").Message(Utc));
        Assert.Equal("Usage request failed (HTTP 500)", AccountIssue.Http(500).Message(Utc));
        Assert.False(AccountIssue.NotConnected.GreysValues);
        Assert.True(AccountIssue.ExpiredCli.GreysValues);
        Assert.Equal("http500", AccountIssue.Http(500).LogName);
        Assert.Equal("network", AccountIssue.Network("secret detail").LogName);
    }
}

public class PersistedStateTests
{
    [Fact]
    public void StateRoundTripsThroughJson()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var snap = new UsageSnapshot(new List<UsageLimit>
        {
            new(LimitKind.Session, 20, now),
            new(LimitKind.Scoped("m", "Model"), 3, null),
        }, new ExtraUsage(true, 5000, 100, 2, "AUD", 2, null), now);
        var state = new PersistedAccountState(snap, now, now, now.AddSeconds(60), "personal@example.com");
        var back = JsonSerializer.Deserialize<PersistedAccountState>(JsonSerializer.Serialize(state, AppJson.Options), AppJson.Options);
        Assert.Equal(state, back);
    }
}

public class TrayModelTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    static UsageSnapshot Snap(double? session, double? weekly, double? scoped = null)
    {
        var limits = new List<UsageLimit>();
        if (session is { } s) limits.Add(new UsageLimit(LimitKind.Session, s, Now.AddHours(1)));
        if (weekly is { } w) limits.Add(new UsageLimit(LimitKind.Weekly, w, Now.AddDays(1)));
        if (scoped is { } sc) limits.Add(new UsageLimit(LimitKind.Scoped("fable", "Fable"), sc, null));
        return new UsageSnapshot(limits, null, Now);
    }

    static List<TrayInput> Inputs(UsageSnapshot? p, UsageSnapshot? w, double wAgeSeconds = 0) => new()
    {
        new TrayInput("P", "Personal", p, p is null ? null : Now),
        new TrayInput("W", "Work", w, w is null ? null : Now.AddSeconds(-wAgeSeconds)),
    };

    static TrayModel Build(List<TrayInput> i, LabelStyle style, MetricSelection? metrics = null) =>
        TrayModel.Build(i, style, metrics ?? new MetricSelection(), Now);

    [Fact]
    public void PercentagesStyle() =>
        Assert.Equal("P 20% / 60%  W 45% / 72%", Build(Inputs(Snap(20, 60), Snap(45, 72)), LabelStyle.Percentages).PlainText);

    [Fact]
    public void OnlyPercentagesSpacesTheSeparator()
    {
        Assert.Equal(" / ", Build(Inputs(Snap(20, 60), null), LabelStyle.Percentages).Separator);
        Assert.Equal("·", Build(Inputs(Snap(20, 60), null), LabelStyle.Compact).Separator);
    }

    [Fact]
    public void CompactStyle() =>
        Assert.Equal("P 20·60  W 45·72", Build(Inputs(Snap(20, 60), Snap(45, 72)), LabelStyle.Compact).PlainText);

    [Fact]
    public void BarsStyleCarriesValuesAndOptionalThirdBar()
    {
        var m = Build(Inputs(Snap(20, 60, 12), Snap(45, 72)), LabelStyle.Bars, new MetricSelection(true, true, true));
        Assert.Equal("P [20|60|12]  W [45|72]", m.PlainText);
        Assert.Equal(3, m.Accounts[0].Values.Count);
    }

    [Fact]
    public void IconOnlyIsAlwaysPlain()
    {
        Assert.Equal("✳", Build(Inputs(Snap(20, 60), Snap(45, 72)), LabelStyle.IconOnly).PlainText);
        Assert.Equal("✳", Build(Inputs(Snap(20, 60), Snap(96, 99)), LabelStyle.IconOnly).PlainText);
    }

    [Fact]
    public void ValuesCarryJustThePercent() =>
        Assert.Equal(new[] { 80, 96 }, Build(Inputs(Snap(79.6, 96), null), LabelStyle.Compact).Accounts[0].Values);

    [Fact]
    public void MetricTogglesAndOrder() =>
        Assert.Equal("P 60·5  W –", Build(Inputs(Snap(20, 60, 5), null), LabelStyle.Compact, new MetricSelection(false, true, true)).PlainText);

    [Fact]
    public void MissingAccountShowsDash()
    {
        var m = Build(Inputs(Snap(20, 60), null), LabelStyle.Percentages);
        Assert.Equal("P 20% / 60%  W –", m.PlainText);
        Assert.True(m.Accounts[1].Missing);
    }

    [Fact]
    public void StaleAfterFifteenMinutes()
    {
        Assert.False(Build(Inputs(Snap(20, 60), Snap(45, 72), 14 * 60), LabelStyle.Compact).Accounts[1].Stale);
        var stale = Build(Inputs(Snap(20, 60), Snap(45, 72), 16 * 60), LabelStyle.Compact);
        Assert.True(stale.Accounts[1].Stale);
        Assert.False(stale.Accounts[0].Stale);
    }

    [Fact]
    public void SingleIconTooltipListsEveryAccount()
    {
        var m = Build(Inputs(Snap(20, 60), Snap(45, 72)), LabelStyle.Bars);
        Assert.Equal("Usage Bar\nPersonal — session 20%, weekly 60%\nWork — session 45%, weekly 72%", m.SingleTooltip());
        Assert.Equal("Usage Bar\nPersonal — session 20%, weekly 60%\nWork — no data", Build(Inputs(Snap(20, 60), null), LabelStyle.Bars).SingleTooltip());
    }

    [Fact]
    public void AccountTooltipShowsThatAccountsLimitsWithResets()
    {
        var tz = TimeZoneInfo.Utc;
        var snap = new UsageSnapshot(new List<UsageLimit>
        {
            new(LimitKind.Session, 20, Now.AddMinutes(219)),
            new(LimitKind.Weekly, 60, Now.AddMinutes(150)),
        }, null, Now);
        var tip = TrayModel.AccountTooltip("Personal", snap, Now, tz);
        Assert.Equal("Usage Bar · Personal\nSession 20% · resets in 3h 39m\nWeekly 60% · resets in 2h 30m", tip);
        Assert.Equal("Usage Bar · Work\nNo data yet", TrayModel.AccountTooltip("Work", null, Now, tz));
    }

    [Fact]
    public void TooltipsFitTheShellLimit()
    {
        var longName = new string('x', 200);
        var tip = TrayModel.AccountTooltip(longName, Snap(20, 60, 5), Now, TimeZoneInfo.Utc);
        Assert.True(tip.Length <= TrayModel.MaxTooltip);
        var m = TrayModel.Build(Enumerable.Range(0, 6).Select(i => new TrayInput("X", longName, Snap(20, 60), Now)).ToList(),
                                LabelStyle.Bars, new MetricSelection(), Now);
        Assert.True(m.SingleTooltip().Length <= TrayModel.MaxTooltip);
    }
}

public class SettingsTests
{
    [Fact]
    public void FreshSettingsDefaults()
    {
        var s = new AppSettings();
        Assert.Equal(LabelStyle.Bars, s.LabelStyle);
        Assert.Equal(TrayIconMode.PerAccount, s.TrayIcons);
        Assert.Equal(DropdownLayout.BothAccounts, s.DropdownLayout);
        Assert.Equal(2, s.RefreshMinutes);
        Assert.Equal("AUD", s.Currency);
        Assert.True(s.ShowSession);
        Assert.True(s.ShowWeekly);
        Assert.False(s.ShowScoped);
        Assert.False(s.DesktopConnected);
        // One account unless the user adds more: no Work account appears without work details.
        Assert.Equal(new[] { "P" }, s.Accounts.Select(a => a.Tag));
        Assert.Equal(SourceKind.ClaudeCode, s.Accounts[0].Source.Kind);
        Assert.Equal("orange", s.Accounts[0].Colour);
    }

    const string OldDefaults = """
    {"accounts":[
      {"id":"00000000-0000-4000-8000-0000000000aa","name":"Personal","tag":"P","colorHex":"#D97757","showInTray":true,
       "source":{"kind":"claudeCode","dir":"~/.claude"},"logsDir":"~/.claude/projects"},
      {"id":"00000000-0000-4000-8000-0000000000bb","name":"Work","tag":"W","colorHex":"#3E7CC4","showInTray":true,
       "source":{"kind":"claudeDesktop"}}
    ]%EXTRA%}
    """;

    [Fact]
    public void SettingsFromZeroPointOneDropTheUntouchedDefaultWorkAccount()
    {
        var s = AppSettings.FromJson(OldDefaults.Replace("%EXTRA%", ""));
        Assert.Equal(new[] { "Personal" }, s.Accounts.Select(a => a.Name));
        Assert.Equal(AppSettings.CurrentDefaultsVersion, s.DefaultsVersion);
        // Once migrated it stays: an account the user adds later is kept.
        s.Accounts.Add(Account.DefaultWork());
        Assert.Equal(2, AppSettings.FromJson(s.ToJson()).Accounts.Count);
    }

    [Fact]
    public void AWorkAccountTheUserTouchedOrConnectedIsKept()
    {
        Assert.Equal(2, AppSettings.FromJson(OldDefaults.Replace("%EXTRA%", ",\"desktopConnected\":true")).Accounts.Count);
        Assert.Equal(2, AppSettings.FromJson(OldDefaults.Replace("\"name\":\"Work\"", "\"name\":\"Job\"").Replace("%EXTRA%", "")).Accounts.Count);
    }

    [Fact]
    public void FixtureAccountsAreNeverSaved()
    {
        var s = new AppSettings();
        s.Accounts.Add(Account.DefaultWork() with { Source = AccountSource.Fixture("C:/fixtures") });
        Assert.Single(AppSettings.FromJson(s.ToJson()).Accounts);
    }

    [Fact]
    public void PartialOrOddJsonKeepsDefaultsForEverythingElse()
    {
        var s = AppSettings.FromJson("""{"labelStyle":"percentages","refreshMinutes":5,"currency":"eur","trayIcons":"nonsense"}""");
        Assert.Equal(LabelStyle.Percentages, s.LabelStyle);
        Assert.Equal(5, s.RefreshMinutes);
        Assert.Equal("EUR", s.Currency);
        Assert.Equal(TrayIconMode.PerAccount, s.TrayIcons);
        Assert.Single(s.Accounts);
        Assert.Equal(new AppSettings().Accounts.Count, AppSettings.FromJson("not json").Accounts.Count);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var s = new AppSettings { LabelStyle = LabelStyle.IconOnly, TrayIcons = TrayIconMode.Single, Currency = "USD" };
        s.Accounts.Add(Account.DefaultWork() with { Name = "Job", ShowInTray = false });
        var back = AppSettings.FromJson(s.ToJson());
        Assert.Equal(LabelStyle.IconOnly, back.LabelStyle);
        Assert.Equal(TrayIconMode.Single, back.TrayIcons);
        Assert.Equal("Job", back.Accounts[1].Name);
        Assert.False(back.Accounts[1].ShowInTray);
        Assert.Equal(s.Accounts[1].Id, back.Accounts[1].Id);
    }

    [Fact]
    public void StyleAndLayoutTitles()
    {
        Assert.Equal(new[] { "Bars", "Percentages", "Icon only" }, LabelStyles.Offered.Select(LabelStyles.Title));
        Assert.Equal("Both accounts", DropdownLayout.BothAccounts.Title());
        Assert.Equal("One at a time", DropdownLayout.OneAtATime.Title());
        Assert.Equal("One per account", TrayIconMode.PerAccount.Title());
        Assert.Equal("Single icon", TrayIconMode.Single.Title());
    }
}
