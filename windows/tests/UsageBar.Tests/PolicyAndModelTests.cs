using System.Text.Json;
using UsageBar.Core;

namespace UsageBar.Tests;

public class FetchPolicyTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T06:30:00Z");

    static bool Should(FetchTrigger t, double? last, double? waitFor = null, double interval = 120, bool token = false) =>
        FetchPolicy.ShouldFetch(t, last is { } l ? Now.AddSeconds(-l) : null,
                                waitFor is { } w ? Now.AddSeconds(w) : null, interval, Now, token);

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

    [Fact]
    public void RetryAfterZeroOrMissingWaitsFiveMinutesAndLongOnesAreCappedAtAnHour()
    {
        Assert.Equal(300, FetchPolicy.RateLimitWait(null));
        Assert.Equal(300, FetchPolicy.RateLimitWait(0));
        Assert.Equal(300, FetchPolicy.RateLimitWait(-5));
        Assert.Equal(30, FetchPolicy.RateLimitWait(30));
        Assert.Equal(3600, FetchPolicy.RateLimitWait(3600));
        Assert.Equal(3600, FetchPolicy.RateLimitWait(7200));
    }

    [Fact]
    public void TimerFetchesOnlyOnceTheIntervalHasNearlyPassed()
    {
        Assert.True(Should(FetchTrigger.Timer, null));
        Assert.False(Should(FetchTrigger.Timer, 60));
        Assert.False(Should(FetchTrigger.Timer, 80));
        Assert.False(Should(FetchTrigger.Timer, 89));
        // 30 s of slack: the 60 s tick fires a little off, and without it the 2-minute setting fires at 3.
        Assert.True(Should(FetchTrigger.Timer, 90));
        Assert.True(Should(FetchTrigger.Timer, 95));
        Assert.True(Should(FetchTrigger.Timer, 119.5));
    }

    [Fact]
    public void TimerAtFiveMinutesSkipsTheEarlierTicks()
    {
        foreach (var elapsed in new double[] { 60, 120, 180, 240, 269 })
            Assert.False(Should(FetchTrigger.Timer, elapsed, interval: 300), elapsed.ToString());
        Assert.True(Should(FetchTrigger.Timer, 270, interval: 300));
        Assert.True(Should(FetchTrigger.Timer, 300, interval: 300));
    }

    [Fact]
    public void LaunchWaitsForTheWholeIntervalWithNoSlack()
    {
        Assert.False(Should(FetchTrigger.Launch, 299, interval: 300));
        Assert.True(Should(FetchTrigger.Launch, 300, interval: 300));
    }

    [Fact]
    public void FlyoutOpenNeverFetchesATokenSource()
    {
        foreach (var last in new double?[] { null, 0, 119, 120, 900, 3600, 86_400 })
            Assert.False(Should(FetchTrigger.MenuOpen, last, interval: 900, token: true), last?.ToString() ?? "null");
    }

    [Fact]
    public void FlyoutOpenStillChecksACookieSourceOnceTwoMinutesHavePassed()
    {
        foreach (var interval in new double[] { 300, 600, 1200 })
        {
            Assert.False(Should(FetchTrigger.MenuOpen, 119, interval: interval), interval.ToString());
            Assert.True(Should(FetchTrigger.MenuOpen, 120, interval: interval), interval.ToString());
        }
        Assert.True(Should(FetchTrigger.MenuOpen, null, interval: 120));
    }

    [Fact]
    public void RefreshNowOnATokenSourceWaitsFiveMinutesAfterTheLastAttempt()
    {
        Assert.True(Should(FetchTrigger.Manual, null, interval: 900, token: true));
        Assert.False(Should(FetchTrigger.Manual, 5, interval: 900, token: true));
        Assert.False(Should(FetchTrigger.Manual, 299, interval: 900, token: true));
        Assert.True(Should(FetchTrigger.Manual, 300, interval: 900, token: true));
        Assert.False(Should(FetchTrigger.Manual, 999, waitFor: 60, interval: 900, token: true));
    }

    [Fact]
    public void RefreshNowOnACookieSourceStillFetchesOutsideAWait()
    {
        Assert.True(Should(FetchTrigger.Manual, 1, interval: 120));
        Assert.False(Should(FetchTrigger.Manual, 1, waitFor: 60, interval: 120));
    }

    [Fact]
    public void ATokenSourceTimerAndLaunchUseTheFifteenMinuteFloor()
    {
        var interval = FetchPolicy.BackgroundInterval(2, tokenSource: true);
        Assert.False(Should(FetchTrigger.Timer, 840, interval: interval, token: true));
        Assert.True(Should(FetchTrigger.Timer, 870, interval: interval, token: true));
        Assert.False(Should(FetchTrigger.Launch, 899, interval: interval, token: true));
        Assert.True(Should(FetchTrigger.Launch, 900, interval: interval, token: true));
        Assert.True(Should(FetchTrigger.Launch, null, interval: interval, token: true));
    }

    [Fact]
    public void FlyoutOpenChecksACookieSourceAtItsIntervalWhenThatIsShorterThanTwoMinutes()
    {
        Assert.False(Should(FetchTrigger.MenuOpen, 59, interval: 60));
        Assert.True(Should(FetchTrigger.MenuOpen, 60, interval: 60));
        Assert.False(Should(FetchTrigger.MenuOpen, 119, interval: 120));
        Assert.True(Should(FetchTrigger.MenuOpen, 120, interval: 120));
    }

    [Fact]
    public void TokenSourcesPollNoFasterThanEveryFifteenMinutes()
    {
        Assert.Equal(900, FetchPolicy.TokenMinimumInterval);
        foreach (var minutes in new[] { 1, 2, 5, 10 })
            Assert.Equal(900, FetchPolicy.BackgroundInterval(minutes, tokenSource: true));
    }

    [Fact]
    public void CookieSourcesPollAtTheChosenInterval()
    {
        Assert.Equal(60, FetchPolicy.BackgroundInterval(1, tokenSource: false));
        Assert.Equal(120, FetchPolicy.BackgroundInterval(2, tokenSource: false));
        Assert.Equal(600, FetchPolicy.BackgroundInterval(10, tokenSource: false));
    }

    [Fact]
    public void AnIntervalOutsideTheSettingsOptionsCountsAsTwoMinutes()
    {
        Assert.Equal(120, FetchPolicy.BackgroundInterval(0, tokenSource: false));
        Assert.Equal(120, FetchPolicy.BackgroundInterval(3, tokenSource: false));
        Assert.Equal(900, FetchPolicy.BackgroundInterval(0, tokenSource: true));
    }

    [Fact]
    public void ClaudeCodeAndTheClaudeAppOnItsTokenAreTokenSources()
    {
        Assert.True(FetchPolicy.IsTokenSource(SourceKind.ClaudeCode, null));
        Assert.True(FetchPolicy.IsTokenSource(SourceKind.ClaudeDesktop, "token"));
        Assert.False(FetchPolicy.IsTokenSource(SourceKind.ClaudeDesktop, "cookie"));
        Assert.False(FetchPolicy.IsTokenSource(SourceKind.ClaudeDesktop, null));
        Assert.False(FetchPolicy.IsTokenSource(SourceKind.Fixture, "fixture"));
        Assert.False(FetchPolicy.IsTokenSource(SourceKind.Fixture, null));
    }

    [Fact]
    public void ClaudeCodeOnTheTwoMinuteSettingWaitsFifteenMinutesBetweenTimerFetches()
    {
        var token = FetchPolicy.IsTokenSource(SourceKind.ClaudeCode, null);
        var interval = FetchPolicy.BackgroundInterval(2, token);
        foreach (var elapsed in new double[] { 120, 300, 600, 869 })
            Assert.False(Should(FetchTrigger.Timer, elapsed, interval: interval, token: token), elapsed.ToString());
        Assert.True(Should(FetchTrigger.Timer, 900, interval: interval, token: token));
    }
}

public class RateLimitJitterTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T06:30:00Z");

    /// <summary>SplitMix64: a small seeded generator, so the jitter tests are repeatable.</summary>
    static Func<ulong> Seeded(ulong state) => () =>
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    };

    [Fact]
    public void JitterIsTwoToTenMinutes()
    {
        Assert.Equal(120, FetchPolicy.RateLimitJitterMin);
        Assert.Equal(600, FetchPolicy.RateLimitJitterMax);
        Assert.Equal(120, FetchPolicy.RateLimitJitter(() => 0));
        var top = FetchPolicy.RateLimitJitter(() => ulong.MaxValue);
        Assert.True(top > 599 && top <= 600, top.ToString());
    }

    [Fact]
    public void SeededJitterStaysInRangeAndSpreadsOut()
    {
        var rng = Seeded(42);
        var draws = Enumerable.Range(0, 2000).Select(_ => FetchPolicy.RateLimitJitter(rng)).ToList();
        Assert.All(draws, d => Assert.InRange(d, 120, 600));
        Assert.True(draws.Min() < 150);
        Assert.True(draws.Max() > 570);
    }

    [Fact]
    public void TheWaitIsRetryAfterPlusTheJitter()
    {
        var report = new FetchReport();
        AccountFetcher.Apply(new FetchOutcome(FetchOutcomeKind.RateLimited, Status: 429, RetryAfter: 3402), report, AccountIssue.ExpiredCli, Now, jitter: 245);
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(3402 + 245)), report.Issue);
        Assert.Equal(245, report.RateLimitJitter);
        AccountFetcher.Apply(new FetchOutcome(FetchOutcomeKind.RateLimited, Status: 429, RetryAfter: 7200), report, AccountIssue.ExpiredCli, Now, jitter: 600);
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(3600 + 600)), report.Issue);
        AccountFetcher.ApplyCookieOutcome(new(CookieOutcomeKind.RateLimited, 429, RetryAfter: 0), report, AccountIssue.ExpiredDesktop,
                                          "the Claude app", "The Claude app", Now, jitter: 120);
        Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(300 + 120)), report.Issue);
        Assert.Equal(120, report.RateLimitJitter);
    }

    [Fact]
    public void AnUnsetJitterIsDrawnFromTheRange()
    {
        for (var i = 0; i < 50; i++)
        {
            var report = new FetchReport();
            AccountFetcher.Apply(new FetchOutcome(FetchOutcomeKind.RateLimited, Status: 429, RetryAfter: 100), report, AccountIssue.ExpiredCli, Now);
            var j = Assert.NotNull(report.RateLimitJitter);
            Assert.InRange(j, 120, 600);
            Assert.Equal(AccountIssue.RateLimited(Now.AddSeconds(100 + j)), report.Issue);
        }
    }
}

public class BackoffTests
{
    [Fact]
    public void Each429DoublesATokenSourceUpToAnHour()
    {
        var b = new FetchBackoff();
        Assert.Equal(900, FetchPolicy.BackgroundInterval(2, true, b.Level));
        b.RateLimited(900);
        Assert.Equal(1, b.Level);
        Assert.Equal(1800, FetchPolicy.BackgroundInterval(2, true, b.Level));
        b.RateLimited(900);
        Assert.Equal(3600, FetchPolicy.BackgroundInterval(2, true, b.Level));
        b.RateLimited(900);
        Assert.Equal(2, b.Level);   // no level past the hour, so one step down is felt
        Assert.Equal(3600, FetchPolicy.BackgroundInterval(2, true, b.Level));
    }

    [Fact]
    public void Each429DoublesACookieSourceUpToAnHour()
    {
        var b = new FetchBackoff();
        var seen = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            b.RateLimited(120);
            seen.Add(FetchPolicy.BackgroundInterval(2, false, b.Level));
        }
        Assert.Equal(new double[] { 240, 480, 960, 1920, 3600, 3600, 3600, 3600 }, seen);
        Assert.Equal(5, b.Level);
        Assert.Equal(3600, FetchPolicy.BackgroundInterval(10, false, 3));
        Assert.Equal(1200, FetchPolicy.BackgroundInterval(10, false, 1));
    }

    [Fact]
    public void ThreeGoodReadsStepBackOneLevel()
    {
        var b = new FetchBackoff(2, 0);
        b.Succeeded(); b.Succeeded();
        Assert.Equal(2, b.Level);
        b.Succeeded();
        Assert.Equal(1, b.Level);
        Assert.Equal(0, b.Successes);
        b.Succeeded(); b.Succeeded(); b.Succeeded();
        Assert.Equal(0, b.Level);
        b.Succeeded(); b.Succeeded(); b.Succeeded();
        Assert.Equal(new FetchBackoff(0, 0), b);
    }

    [Fact]
    public void A429OrAnyOtherFailureRestartsTheCount()
    {
        var b = new FetchBackoff(1, 0);
        b.Succeeded(); b.Succeeded();
        b.Failed();
        b.Succeeded(); b.Succeeded();
        Assert.Equal(1, b.Level);   // the failure broke the run of good reads
        b.RateLimited(900);
        Assert.Equal(new FetchBackoff(2, 0), b);
    }

    [Fact]
    public void TheHighestLevelIsTheOneThatReachesAnHour()
    {
        Assert.Equal(2, FetchPolicy.MaxBackoffLevel(900));
        Assert.Equal(5, FetchPolicy.MaxBackoffLevel(120));
        Assert.Equal(6, FetchPolicy.MaxBackoffLevel(60));
        Assert.Equal(4, FetchPolicy.MaxBackoffLevel(300));
        Assert.Equal(3, FetchPolicy.MaxBackoffLevel(600));
        Assert.Equal(0, FetchPolicy.MaxBackoffLevel(0));
    }
}

public class NextCheckTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T06:30:00Z");

    [Fact]
    public void TheNextCheckIsTheLastAttemptPlusTheInterval() =>
        Assert.Equal(Now.AddSeconds(780), FetchPolicy.NextCheck(Now.AddSeconds(-120), null, 900, Now));

    [Fact]
    public void TheNextCheckWaitsForARateLimitWait() =>
        Assert.Equal(Now.AddSeconds(1800), FetchPolicy.NextCheck(Now.AddSeconds(-120), Now.AddSeconds(1800), 900, Now));

    [Fact]
    public void TheNextCheckIsNeverInThePast()
    {
        Assert.Equal(Now, FetchPolicy.NextCheck(Now.AddSeconds(-5000), Now.AddSeconds(-10), 900, Now));
        Assert.Equal(Now, FetchPolicy.NextCheck(null, null, 900, Now));
    }
}

public class StalenessTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T06:30:00Z");

    static bool Stale(double? age, double interval, double? waitEndsIn = null) =>
        TrayModel.IsStale(age is { } a ? Now.AddSeconds(-a) : null, interval, waitEndsIn is { } w ? Now.AddSeconds(w) : null, Now);

    [Fact]
    public void AHealthyTokenAccountNeverGreysBetweenChecks()
    {
        // Checks every 15 minutes (up to a minute late on the tick): never more than about 16 minutes old.
        foreach (var age in new double[] { 0, 14 * 60, 16 * 60, 29 * 60, 30 * 60 })
            Assert.False(Stale(age, 900), age.ToString());
        Assert.True(Stale(30 * 60 + 1, 900));   // two missed checks
    }

    [Fact]
    public void ABackedOffAccountGreysOnlyAfterTwoMissedIntervals()
    {
        Assert.False(Stale(61 * 60, 3600));
        Assert.False(Stale(120 * 60, 3600));
        Assert.True(Stale(121 * 60, 3600));
        Assert.False(Stale(59 * 60, 1800));
        Assert.True(Stale(61 * 60, 1800));
    }

    [Fact]
    public void AFastCookieAccountStillGreysAfterFifteenMinutes()
    {
        Assert.False(Stale(15 * 60, 120));
        Assert.True(Stale(15 * 60 + 1, 120));
        Assert.True(Stale(16 * 60, 60));
    }

    [Fact]
    public void ARateLimitWaitDoesNotGreyUntilFifteenMinutesAfterItEnds()
    {
        // Last good read 70 minutes ago; the wait ends in 10 minutes.
        Assert.False(Stale(70 * 60, 900, 10 * 60));
        // The wait ended 14 minutes ago and nothing has worked since: not yet.
        Assert.False(Stale(70 * 60, 900, -14 * 60));
        // 16 minutes after the wait ended: the retry plainly failed.
        Assert.True(Stale(70 * 60, 900, -16 * 60));
    }

    [Fact]
    public void NoDataIsNeverStale() => Assert.False(Stale(null, 900));

    [Fact]
    public void TheTrayGreysByEachAccountsOwnSchedule()
    {
        var snap = new UsageSnapshot(new List<UsageLimit> { new(LimitKind.Session, 20, null) }, null, Now);
        var ago = Now.AddMinutes(-25);
        var m = TrayModel.Build(new List<TrayInput>
        {
            new("P", "Personal", snap, ago, 900),
            new("W", "Work", snap, ago, 120),
            new("C", "Claude app", snap, Now.AddMinutes(-80), 900, Now.AddSeconds(60)),
        }, LabelStyle.Compact, new MetricSelection(true, true, false), Now);
        Assert.Equal(new[] { false, true, false }, m.Accounts.Select(a => a.Stale));
    }
}

public class RateLimitPresentationTests
{
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly DateTimeOffset Until = DateTimeOffset.Parse("2026-10-26T06:40:00Z");
    /// <summary>No-break spaces keep the time with its phrase when the header wraps.</summary>
    const string Nb = "\u00A0";

    [Fact]
    public void RateLimitWithDataKeepsNumbersAndNotesItInTheHeader()
    {
        var p = new AccountPresentation(AccountIssue.RateLimited(Until), true, timeZone: Utc);
        Assert.Equal($"Rate limited · next check{Nb}6:40{Nb}am", p.HeaderNote);
        Assert.Null(p.InlineIssue);
        Assert.False(p.GreysValues);
    }

    [Fact]
    public void RateLimitWithoutDataIsShownAsAnError()
    {
        var p = new AccountPresentation(AccountIssue.RateLimited(Until), false, timeZone: Utc);
        Assert.Null(p.HeaderNote);
        Assert.Equal(AccountIssue.RateLimited(Until), p.InlineIssue);
        Assert.Equal("Rate limited — retrying after 6:40 am", AccountIssue.RateLimited(Until).Message(Utc));
    }

    [Fact]
    public void OtherIssuesStillShowInlineAndGreyOldNumbers()
    {
        var p = new AccountPresentation(AccountIssue.Network("offline"), true, timeZone: Utc);
        Assert.Null(p.HeaderNote);
        Assert.Equal(AccountIssue.Network("offline"), p.InlineIssue);
        Assert.True(p.GreysValues);
    }

    [Fact]
    public void NoIssueShowsNothing()
    {
        var p = new AccountPresentation(null, true, timeZone: Utc);
        Assert.Null(p.HeaderNote);
        Assert.Null(p.InlineIssue);
        Assert.False(p.GreysValues);
    }

    [Fact]
    public void ASkippedRefreshNowSaysWhenTheNextCheckIs()
    {
        var p = new AccountPresentation(null, true, Until, Utc);
        Assert.Equal($"Next check{Nb}6:40{Nb}am", p.HeaderNote);
        Assert.False(p.GreysValues);
        Assert.Equal($"Next check{Nb}6:40{Nb}am", new AccountPresentation(null, false, Until, Utc).HeaderNote);
        // Inside a wait the rate-limit note already gives the next check.
        Assert.Equal($"Rate limited · next check{Nb}6:40{Nb}am",
                     new AccountPresentation(AccountIssue.RateLimited(Until), true, Until, Utc).HeaderNote);
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

    [Fact]
    public void TheWaitBackoffAndRouteSurviveARelaunch()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var state = new PersistedAccountState(null, now.AddSeconds(-300), now, now.AddSeconds(3647), null, new FetchBackoff(2, 1), "token");
        var json = JsonSerializer.Serialize(new Dictionary<Guid, PersistedAccountState> { [Guid.Empty] = state }, AppJson.Options);
        var back = JsonSerializer.Deserialize<Dictionary<Guid, PersistedAccountState>>(json, AppJson.Options)![Guid.Empty];
        Assert.Equal(state, back);
        Assert.Equal(new FetchBackoff(2, 1), back.Backoff);
        Assert.Equal("token", back.LastRoute);
        // And the restored wait still blocks every trigger.
        foreach (var t in Enum.GetValues<FetchTrigger>())
            Assert.False(FetchPolicy.ShouldFetch(t, back.LastAttempt, back.WaitUntil, 900, now.AddSeconds(3600), tokenSource: true), t.ToString());
    }

    [Fact]
    public void StateSavedByAnEarlierVersionStillLoads()
    {
        // 0.1.6 wrote only these keys, with dates in this form; a missing key must not drop the file.
        const string json = """
            {
              "00000000-0000-4000-8000-0000000000aa": {
                "lastSuccess": "2026-10-27T18:28:37.1234567+10:00",
                "lastAttempt": "2026-10-27T18:28:37.123456+10:00",
                "waitUntil": "2026-10-27T19:28:37.123456+10:00"
              },
              "00000000-0000-4000-8000-0000000000bb": { "lastAttempt": "2026-10-27T18:30:46.5+10:00" }
            }
            """;
        var saved = JsonSerializer.Deserialize<Dictionary<Guid, PersistedAccountState>>(json, AppJson.Options)!;
        Assert.Equal(2, saved.Count);
        var a = saved[Guid.Parse("00000000-0000-4000-8000-0000000000aa")];
        Assert.Null(a.Backoff);
        Assert.Null(a.LastRoute);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T19:28:37.123456+10:00"), a.WaitUntil);
        Assert.Null(saved[Guid.Parse("00000000-0000-4000-8000-0000000000bb")].WaitUntil);
    }

    [Fact]
    public void AWaitSavedByAnEarlierVersionGetsAJitterAndCountsAsOne429()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var until = now.AddSeconds(2700);
        var st = new PersistedAccountState(null, null, now.AddSeconds(-700), until, null);
        Assert.True(st.UpgradeEarlierWait(900, 300, now, out var upgraded));
        Assert.Equal(until.AddSeconds(300), upgraded.WaitUntil);
        Assert.Equal(new FetchBackoff(1, 0), upgraded.Backoff);
        // Once upgraded (the backoff is saved with it), it is never moved again.
        Assert.False(upgraded.UpgradeEarlierWait(900, 300, now, out var again));
        Assert.Equal(until.AddSeconds(300), again.WaitUntil);
    }

    [Fact]
    public void APassedWaitFromAnEarlierVersionIsLeftAlone()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var passed = new PersistedAccountState(null, null, null, now.AddSeconds(-1), null);
        Assert.False(passed.UpgradeEarlierWait(900, 300, now, out var same));
        Assert.Equal(now.AddSeconds(-1), same.WaitUntil);
        Assert.Null(same.Backoff);
        Assert.False(new PersistedAccountState(null, null, null, null, null).UpgradeEarlierWait(900, 300, now, out _));
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
        // Accounts come only from a first run's detection or the user (decision 16).
        var s = new AppSettings();
        Assert.Empty(s.Accounts);
        Assert.Equal(LabelStyle.Bars, s.LabelStyle);
        Assert.Equal(TrayIconMode.PerAccount, s.TrayIcons);
        Assert.Equal(DropdownLayout.BothAccounts, s.DropdownLayout);
        Assert.Equal(2, s.RefreshMinutes);
        Assert.Equal("AUD", s.Currency);
        Assert.True(s.ShowSession);
        Assert.True(s.ShowWeekly);
        Assert.False(s.ShowScoped);
        Assert.False(s.DesktopConnected);
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
    public void SavedSettingsLoadWithTheirAccountsUnchanged()
    {
        // 0.1.0's untouched Work account is kept too: no migration adds, removes or renames accounts.
        var s = AppSettings.FromJson(OldDefaults.Replace("%EXTRA%", ",\"defaultsVersion\":0"))!;
        Assert.Equal(new[] { "Personal", "Work" }, s.Accounts.Select(a => a.Name));
        Assert.Equal(new[] { "P", "W" }, s.Accounts.Select(a => a.Tag));
        Assert.Equal(Guid.Parse("00000000-0000-4000-8000-0000000000bb"), s.Accounts[1].Id);
        var back = AppSettings.FromJson(s.ToJson())!;
        Assert.Equal(s.Accounts, back.Accounts);
    }

    [Fact]
    public void SavedSettingsWithoutAnAccountListKeepPersonal()
    {
        // Every Windows version saved its accounts; a file without the list falls back to the one account all of them started with.
        var s = AppSettings.FromJson("""{"labelStyle":"percentages"}""")!;
        Assert.Equal(new[] { "P" }, s.Accounts.Select(a => a.Tag));
        Assert.Equal(SourceKind.ClaudeCode, s.Accounts[0].Source.Kind);
    }

    [Fact]
    public void FixtureAccountsAreNeverSaved()
    {
        var s = new AppSettings();
        s.Accounts.Add(Account.DefaultPersonal());
        s.Accounts.Add(Account.DefaultWork() with { Source = AccountSource.Fixture("C:/fixtures") });
        Assert.Single(AppSettings.FromJson(s.ToJson())!.Accounts);
    }

    [Fact]
    public void PartialOrOddJsonKeepsDefaultsForEverythingElse()
    {
        var s = AppSettings.FromJson("""{"labelStyle":"percentages","refreshMinutes":5,"currency":"eur","trayIcons":"nonsense"}""")!;
        Assert.Equal(LabelStyle.Percentages, s.LabelStyle);
        Assert.Equal(5, s.RefreshMinutes);
        Assert.Equal("EUR", s.Currency);
        Assert.Equal(TrayIconMode.PerAccount, s.TrayIcons);
    }

    [Fact]
    public void UnreadableSettingsAreAFirstRun()
    {
        Assert.Null(AppSettings.FromJson("not json"));
        Assert.Null(AppSettings.FromJson("[1,2]"));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var s = new AppSettings { LabelStyle = LabelStyle.IconOnly, TrayIcons = TrayIconMode.Single, Currency = "USD" };
        s.Accounts.Add(Account.DefaultPersonal());
        s.Accounts.Add(Account.DefaultClaudeApp() with { Name = "Job", ShowInTray = false });
        var back = AppSettings.FromJson(s.ToJson())!;
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
        // The saved value stays bothAccounts; it shows every account.
        Assert.Equal("All accounts", DropdownLayout.BothAccounts.Title());
        Assert.Equal("One at a time", DropdownLayout.OneAtATime.Title());
        Assert.Equal("One per account", TrayIconMode.PerAccount.Title());
        Assert.Equal("Single icon", TrayIconMode.Single.Title());
    }
}
