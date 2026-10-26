using UsageBar.Core;

namespace UsageBar;

/// <summary>
/// Built-in sample data for demo mode (<c>--demo</c>): the mockup's numbers and made-up identities, the same as the
/// macOS DemoData. Used for screenshots, so it must never contain real data.
/// </summary>
internal static class DemoData
{
    public const string PersonalEmail = "personal@example.com";
    public const string WorkOrganization = "Example Co";

    /// <summary>Default settings with fixed account ids, so Windows keeps one tray entry per demo icon across launches.</summary>
    public static AppSettings Settings()
    {
        var s = new AppSettings();
        s.Accounts[0] = s.Accounts[0] with { Id = Guid.Parse("00000000-0000-4000-8000-0000000000aa") };
        s.Accounts.Add(Account.DefaultWork() with { Id = Guid.Parse("00000000-0000-4000-8000-0000000000bb") });
        return s;
    }

    public static ExchangeRates Rates(DateTimeOffset now) => new(new Dictionary<string, double>
    {
        ["USD"] = 1, ["AUD"] = 1.42, ["EUR"] = 0.92, ["GBP"] = 0.79, ["NZD"] = 1.66, ["CAD"] = 1.37, ["JPY"] = 151.3,
    }, now, now);

    /// <summary>A limit <paramref name="used"/> percent through a window, <paramref name="expected"/> percent of the way through it.</summary>
    static UsageLimit Limit(LimitKind kind, double used, double expected, DateTimeOffset now)
    {
        var window = kind.Type == LimitKindType.Session ? 300.0 : 10080.0;
        return new UsageLimit(kind, used, now.AddMinutes(window * (1 - expected / 100)));
    }

    public static UsageSnapshot Personal(DateTimeOffset now) => new(new List<UsageLimit>
    {
        Limit(LimitKind.Session, 20, 27, now),
        Limit(LimitKind.Weekly, 60, 71, now),
        Limit(LimitKind.Scoped("demo-model", "Fable"), 12, 71, now),
    }, new ExtraUsage(true, 5000, 1250, 25, "AUD", 2, null), now);

    public static UsageSnapshot Work(DateTimeOffset now) => new(new List<UsageLimit>
    {
        Limit(LimitKind.Session, 45, 57, now),
        Limit(LimitKind.Weekly, 72, 60, now),
        Limit(LimitKind.Scoped("demo-model", "Fable"), 4, 60, now),
    }, null, now);

    /// <summary>Mockup figures in AUD (A$6.20 today, A$214 over 30 days), stored as USD like real summaries.</summary>
    public static CostSummary PersonalCost(double audPerUsd)
    {
        double[] shape = { 0.10, 0, 0.22, 0.35, 0.15, 0, 0.05, 0.48, 0.62, 0.30, 0.12, 0, 0, 0.27, 0.55,
                           0.71, 0.40, 0.18, 0.09, 0, 0.33, 0.95, 0.58, 0.21, 0.14, 0, 0.36, 0.44, 0.26, 0.30 };
        const double thirtyDayAud = 214.0, todayAud = 6.20;
        var total = shape.Sum();
        var s = new CostSummary { DailyUsd = shape.Select(x => x / total * thirtyDayAud / audPerUsd).ToArray() };
        s.DailyUsd[29] = todayAud / audPerUsd;
        s.TodayUsd = todayAud / audPerUsd;
        s.Last30Usd = thirtyDayAud / audPerUsd;
        s.TodayTokens = 9_000_000;
        s.Last30Tokens = 260_000_000;
        s.TopModel = "claude-opus-5-5";
        return s;
    }
}
