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

    /// <summary>
    /// Personal then Work, the README pair (<c>--demo</c>), or the first <paramref name="count"/> of them
    /// (<c>--demo-accounts 0|1|2</c>). Fixed ids, so Windows keeps one tray entry per demo icon across launches.
    /// </summary>
    public static AppSettings Settings(int count = 2)
    {
        var all = new[]
        {
            Account.DefaultPersonal() with { Id = Guid.Parse("00000000-0000-4000-8000-0000000000aa") },
            Account.DefaultWork() with { Id = Guid.Parse("00000000-0000-4000-8000-0000000000bb") },
        };
        return new AppSettings { Accounts = all.Take(Math.Clamp(count, 0, all.Length)).ToList() };
    }

    /// <summary><c>--scenario edge</c>: eight accounts covering every awkward state the flyout and Settings must lay out, and enough to scroll.</summary>
    public static AppSettings EdgeSettings()
    {
        var s = new AppSettings();
        string[] ids = { "aa", "bb", "cc", "dd", "07", "08", "09", "0a" };
        (string Name, string Tag, string Colour, AccountSource Source)[] defs =
        {
            ("Personal account with a very long name for testing", "P", "orange", AccountSource.ClaudeCode()),
            ("Work", "W", "blue", AccountSource.ClaudeDesktop),
            ("Side project", "SP", "green", AccountSource.ClaudeDesktop),
            ("Client", "C", "purple", AccountSource.ClaudeDesktop),
            ("Team", "T", "teal", AccountSource.ClaudeDesktop),
            ("Extra", "X", "red", AccountSource.ClaudeDesktop),
            ("Archive", "AR", "pink", AccountSource.ClaudeDesktop),
            ("Lab", "L", "lime", AccountSource.ClaudeDesktop),
        };
        s.Accounts = defs.Select((d, i) => new Account
        {
            Id = Guid.Parse("00000000-0000-4000-8000-0000000000" + ids[i]), Name = d.Name, Tag = d.Tag, Colour = d.Colour,
            Source = d.Source, LogsDir = i == 0 ? "~/.claude/projects" : null,
        }).ToList();
        return s;
    }

    public static UsageSnapshot OverLimit(DateTimeOffset now) => new(new List<UsageLimit>
    {
        new(LimitKind.Session, 105, now.AddMinutes(61)),
        new(LimitKind.Weekly, 100, now.AddDays(2)),
        new(LimitKind.Scoped("demo-model", "Fable"), 0, now.AddDays(2)),
    }, new ExtraUsage(false, 15000, 2040, 13.6, "AUD", 2, "out_of_credits"), now);

    public static UsageSnapshot Unstarted(DateTimeOffset now) => new(new List<UsageLimit>
    {
        new(LimitKind.Session, 0, null),
        new(LimitKind.Weekly, 3, null),
    }, null, now);

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
