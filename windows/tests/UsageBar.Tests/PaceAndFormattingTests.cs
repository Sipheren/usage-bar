using UsageBar.Core;

namespace UsageBar.Tests;

public class PaceTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T06:00:00Z");
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void ExpectedMatchesWorkedExample()
    {
        // 282 of 300 minutes elapsed -> expected 94%.
        var p = Pace.Compute(50, Now.AddMinutes(18), 300, Now);
        Assert.Equal(94, p.Expected, 4);
        Assert.Equal(282, p.ElapsedMinutes, 4);
        Assert.Equal(0.94, p.TickFraction, 4);
    }

    [Fact]
    public void WithinTwoPointsIsOnPace()
    {
        // 150 of 300 elapsed -> expected 50.
        var reset = Now.AddMinutes(150);
        foreach (var used in new[] { 48.0, 50, 52 })
        {
            var p = Pace.Compute(used, reset, 300, Now);
            Assert.Equal(PaceStatus.OnPace, p.Status);
            Assert.Equal("On pace · lasts until reset", p.Line(Now, Utc));
        }
        // 2.4 points shows as "2", so it is still on pace; 2.6 shows as "3".
        Assert.Equal(PaceStatus.OnPace, Pace.Compute(52.4, reset, 300, Now).Status);
        Assert.Equal(PaceStatus.OnPace, Pace.Compute(47.6, reset, 300, Now).Status);
        Assert.Equal(PaceStatus.Under, Pace.Compute(47.4, reset, 300, Now).Status);
        Assert.Equal(PaceStatus.Over, Pace.Compute(52.6, reset, 300, Now).Status);
    }

    [Fact]
    public void UnderPaceText()
    {
        var p = Pace.Compute(20, Now.AddMinutes(150), 300, Now);
        Assert.Equal(-30, p.Delta, 3);
        Assert.Equal("30% under pace · lasts until reset", p.Line(Now, Utc));
        Assert.False(p.IsOver);
    }

    [Fact]
    public void OverPaceRunsOutBeforeReset()
    {
        // Weekly: 60% of 10080 min elapsed (6048), 72% used -> 12 over.
        var reset = Now.AddMinutes(10080 - 6048);
        var p = Pace.Compute(72, reset, 10080, Now);
        Assert.Equal(12, p.Delta, 3);
        // rate = 72/6048 %/min; 28% left -> 2352 min = 39.2 h after now.
        Assert.NotNull(p.RunOut);
        Assert.Equal(2352 * 60, (p.RunOut!.Value - Now).TotalSeconds, 0);
        // now 06:00Z Sun + 39h12m = Mon 21:12Z -> rounded to 21:10.
        Assert.Equal("12% over pace · runs out around Mon 9:10 pm", p.Line(Now, Utc));
        Assert.True(p.IsOver);
    }

    [Fact]
    public void OverPaceAlwaysRunsOutBeforeReset()
    {
        foreach (var (elapsed, used) in new[] { (100.0, 40.0), (200.0, 70.0), (280.0, 97.0), (10.0, 6.0) })
        {
            var reset = Now.AddMinutes(300 - elapsed);
            var p = Pace.Compute(used, reset, 300, Now);
            Assert.Equal(PaceStatus.Over, p.Status);
            Assert.True(p.RunOut < reset);
            Assert.Contains("runs out around", p.Line(Now, Utc));
        }
    }

    [Fact]
    public void LimitReachedWhenAtHundred()
    {
        var p = Pace.Compute(100, Now.AddMinutes(60), 300, Now);
        Assert.Equal("20% over pace · limit reached", p.Line(Now, Utc));
    }
}

public class FormattingTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T06:00:00Z");   // Sunday
    static readonly TimeZoneInfo Brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");

    [Fact]
    public void ResetUnder24HoursIsRelative()
    {
        Assert.Equal("in 2h 10m", Formatters.ResetPhrase(Now.AddMinutes(130), Now));
        Assert.Equal("in 2h 10m", Formatters.ResetPhrase(Now.AddSeconds(129 * 60 + 30), Now));
        Assert.Equal("in 45m", Formatters.ResetPhrase(Now.AddMinutes(45), Now));
        Assert.Equal("in 3h", Formatters.ResetPhrase(Now.AddHours(3), Now));
        Assert.Equal("in 23h 59m", Formatters.ResetPhrase(Now.AddMinutes(23 * 60 + 59), Now));
        Assert.Equal("now", Formatters.ResetPhrase(Now.AddSeconds(-5), Now));
    }

    [Fact]
    public void ResetFromOneDayIsWeekdayAndTimeInLocalZone()
    {
        // Thu 29 Oct 2026 09:00 Brisbane = Wed 28 Oct 23:00Z.
        var thu9 = DateTimeOffset.Parse("2026-10-28T23:00:00Z");
        Assert.Equal("Thu 9:00 am", Formatters.ResetPhrase(thu9, Now, Brisbane));
        var mon2pm = DateTimeOffset.Parse("2026-10-26T04:00:00Z");   // Mon 14:00 Brisbane
        Assert.Equal("Mon 2:00 pm", Formatters.ResetPhrase(mon2pm.AddDays(7), Now, Brisbane));
        Assert.Equal("Mon 4:00 pm", Formatters.ResetPhrase(Now.AddHours(24), Now, Brisbane));
        // The API sends e.g. 14:59:59.6Z for a 15:00Z reset: round to the nearest minute, don't truncate.
        var justBefore = DateTimeOffset.Parse("2026-10-28T22:59:59Z").AddMilliseconds(600);
        Assert.Equal("Thu 9:00 am", Formatters.ResetPhrase(justBefore, Now, Brisbane));
        var justAfter = DateTimeOffset.Parse("2026-10-28T23:00:20Z");
        Assert.Equal("Thu 9:00 am", Formatters.ResetPhrase(justAfter, Now, Brisbane));
    }

    [Fact]
    public void Money()
    {
        Assert.Equal("A$6.20", Formatters.Money(6.2));
        Assert.Equal("A$41.80", Formatters.Money(41.8));
        Assert.Equal("A$214", Formatters.Money(214.4));
        Assert.Equal("A$1,050", Formatters.Money(1050.2));
        Assert.Equal("A$0.00", Formatters.Money(0));
    }

    [Fact]
    public void Tokens()
    {
        Assert.Equal("260M", Formatters.Tokens(260_000_000));
        Assert.Equal("1.4B", Formatters.Tokens(1_400_000_000));
        Assert.Equal("9M", Formatters.Tokens(9_000_000));
        Assert.Equal("9.2M", Formatters.Tokens(9_240_000));
        Assert.Equal("86M", Formatters.Tokens(86_000_000));
        Assert.Equal("950", Formatters.Tokens(950));
        Assert.Equal("47K", Formatters.Tokens(47_230));
        Assert.Equal("101K", Formatters.Tokens(100_570));
    }

    [Fact]
    public void UpdatedAgo()
    {
        Assert.Equal("Updated just now", Formatters.UpdatedAgo(Now.AddSeconds(-20), Now));
        Assert.Equal("Updated 2 min ago", Formatters.UpdatedAgo(Now.AddSeconds(-125), Now));
        Assert.Equal("Updated 3 h ago", Formatters.UpdatedAgo(Now.AddHours(-3), Now));
        Assert.Equal("Not updated yet", Formatters.UpdatedAgo(null, Now));
    }

    [Fact]
    public void ExtraUsageEnabledHasNoOffSuffix()
    {
        var e = new ExtraUsage(true, 10000, 1250, 12.5, "AUD", 2, null);
        Assert.Equal("Extra usage A$12.50 of A$100", Formatters.ExtraUsageLine(e));
    }

    [Fact]
    public void ClockTimeIsLowercaseAmPm()
    {
        Assert.Equal("3:02 pm", Formatters.ClockTime(DateTimeOffset.Parse("2026-03-02T05:02:00Z"), Brisbane));
        Assert.Equal("12:05 am", Formatters.ClockTime(DateTimeOffset.Parse("2026-03-01T14:05:00Z"), Brisbane));
    }
}

public class LimitDisplayTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T06:00:00Z");
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void LimitWithResetShowsPercentResetAndPace()
    {
        var d = new LimitDisplay(new UsageLimit(LimitKind.Session, 20, Now.AddMinutes(150)), Now, Utc);
        Assert.Equal("20%", d.PercentText);
        Assert.Equal("resets in 2h 30m", d.CompactTrailing);
        Assert.Equal("Resets in 2h 30m", d.LargeTrailing);
        Assert.Equal("30% under pace · lasts until reset", d.PaceLine);
        Assert.NotNull(d.TickFraction);
    }

    [Fact]
    public void UnstartedSessionSaysStartsOnFirstUseAndHasNoPace()
    {
        var d = new LimitDisplay(new UsageLimit(LimitKind.Session, 0, null), Now, Utc);
        Assert.Equal("0%", d.PercentText);
        Assert.Equal("starts on first use", d.CompactTrailing);
        Assert.Equal("Starts on first use", d.LargeTrailing);
        Assert.Null(d.PaceLine);
        Assert.Null(d.TickFraction);
        Assert.False(d.IsOverPace);
    }

    [Fact]
    public void NoResetButSomeUsageShowsJustThePercent()
    {
        var d = new LimitDisplay(new UsageLimit(LimitKind.Weekly, 12, null), Now, Utc);
        Assert.Null(d.CompactTrailing);
        Assert.Null(d.LargeTrailing);
        Assert.Null(d.PaceLine);
    }

    [Fact]
    public void ScopedLimitUsesTheSamePaceLogicAsTheOthers()
    {
        // 4000 of 10080 minutes left -> expected 60.3%; 12% used -> 48 under.
        var d = new LimitDisplay(new UsageLimit(LimitKind.Scoped("fable", "Fable"), 12, Now.AddMinutes(4000)), Now, Utc);
        Assert.Equal("48% under pace · lasts until reset", d.PaceLine);
    }

    [Fact]
    public void OverPaceFlag()
    {
        var d = new LimitDisplay(new UsageLimit(LimitKind.Weekly, 72, Now.AddMinutes(10080 - 6048)), Now, Utc);
        Assert.True(d.IsOverPace);
    }

    [Fact]
    public void PercentRoundsHalfAwayFromZeroLikeTheMac()
    {
        // Swift's rounded() goes half away from zero; .NET's default would give 32 and 34 here.
        Assert.Equal("33%", new LimitDisplay(new UsageLimit(LimitKind.Weekly, 32.5, null), Now, Utc).PercentText);
        Assert.Equal("35%", new LimitDisplay(new UsageLimit(LimitKind.Weekly, 34.5, null), Now, Utc).PercentText);
    }
}

public class CurrencyTests
{
    static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-10-26T00:10:00Z");
    static readonly ExchangeRates Fetched = new(new Dictionary<string, double>
        { ["USD"] = 1, ["AUD"] = 1.4231, ["EUR"] = 0.92, ["JPY"] = 151.3, ["CHF"] = 0.88 }, AsOf, AsOf);

    [Fact]
    public void UsdNeedsNoRateAndNoConversion()
    {
        Assert.False(Currency.NeedsRate("USD"));
        Assert.Equal(12.5, Currency.Convert(12.5, "USD", ExchangeRates.Fallback));
        Assert.Equal(12.5, Currency.Convert(12.5, "USD", new ExchangeRates(new Dictionary<string, double>(), AsOf, AsOf)));
    }

    [Fact]
    public void ConvertsWithTheChosenRate()
    {
        Assert.True(Currency.NeedsRate("AUD"));
        Assert.Equal(14.231, Currency.Convert(10, "AUD", Fetched)!.Value, 9);
        Assert.Equal(9.2, Currency.Convert(10, "EUR", Fetched)!.Value, 9);
        Assert.Null(Currency.Convert(10, "GBP", Fetched));
    }

    [Fact]
    public void BuiltInRateKeepsAudWorkingBeforeTheFirstFetch()
    {
        Assert.Equal(14.23, Currency.Convert(10, "AUD", ExchangeRates.Fallback)!.Value, 9);
        Assert.True(ExchangeRates.Fallback.IsFallback);
    }

    [Fact]
    public void FormattingUsesSymbolAndDecimals()
    {
        Assert.Equal("A$6.20", Currency.Format(6.2, "AUD"));
        Assert.Equal("A$1,050", Currency.Format(1050.2, "AUD"));
        Assert.Equal("US$6.20", Currency.Format(6.2, "USD"));
        Assert.Equal("€214", Currency.Format(214.4, "EUR"));
        Assert.Equal("£9.50", Currency.Format(9.5, "GBP"));
        Assert.Equal("NZ$9.50", Currency.Format(9.5, "NZD"));
        Assert.Equal("C$9.50", Currency.Format(9.5, "CAD"));
        Assert.Equal("¥938", Currency.Format(938.06, "JPY"));
        Assert.Equal("¥32,380", Currency.Format(32_380.4, "JPY"));
        Assert.Equal("CHF 9.50", Currency.Format(9.5, "CHF"));
    }

    [Fact]
    public void WholeAmountFormatForChartLabels()
    {
        Assert.Equal("A$25", Currency.FormatWhole(24.6, "AUD"));
        Assert.Equal("¥3,725", Currency.FormatWhole(3_725.1, "JPY"));
    }

    [Fact]
    public void RoundingMatchesTheMacNumberFormatter()
    {
        // NSNumberFormatter rounds half to even.
        Assert.Equal("US$0.12", Currency.Format(0.125, "USD"));
        Assert.Equal("US$0.38", Currency.Format(0.375, "USD"));
    }

    [Fact]
    public void OptionsAddTheLocaleCurrencyOnlyWhenTheRateListHasIt()
    {
        var b = new[] { "AUD", "USD", "EUR", "GBP", "NZD", "CAD", "JPY" };
        var available = Fetched.Rates.Keys.ToHashSet();
        Assert.Equal(b, Currency.Options("AUD", available));
        Assert.Equal(b.Append("CHF"), Currency.Options("CHF", available));
        Assert.Equal(b, Currency.Options("SEK", available));
        Assert.Equal(b, Currency.Options(null, new HashSet<string>()));
    }

    [Fact]
    public void RateLineOnlyWhenConverting()
    {
        var utc = TimeZoneInfo.Utc;
        Assert.Null(Formatters.RateLine(Fetched, "USD", utc));
        Assert.Equal("US$1 = A$1.42 · updated 26 Oct", Formatters.RateLine(Fetched, "AUD", utc));
        Assert.Equal("US$1 = ¥151 · updated 26 Oct", Formatters.RateLine(Fetched, "JPY", utc));
        Assert.Equal("US$1 = A$1.42 · built-in rate", Formatters.RateLine(ExchangeRates.Fallback, "AUD", utc));
        Assert.Equal("Waiting for the exchange rate", Formatters.RateLine(ExchangeRates.Fallback, "EUR", utc));
    }

    [Fact]
    public void ParsesTheExchangeRateResponse()
    {
        var json = """{"result":"success","time_last_update_unix":1792972801,"rates":{"USD":1,"AUD":1.4231,"EUR":0.92,"BAD":-1}}""";
        var r = ExchangeRates.Parse(System.Text.Encoding.UTF8.GetBytes(json), AsOf)!;
        Assert.Equal(1.4231, r.Rates["AUD"]);
        Assert.False(r.Rates.ContainsKey("BAD"));
        Assert.Equal(1792972801, r.AsOf.ToUnixTimeSeconds());
        Assert.False(r.IsFallback);
        Assert.Null(ExchangeRates.Parse(System.Text.Encoding.UTF8.GetBytes("{}"), AsOf));
    }
}
