using System.Windows.Media;
using UsageBar.Core;

namespace UsageBar;

internal sealed record LimitVm(string Name, string PercentText, string? CompactTrailing, string LargeTitle, string? LargeTrailing,
                               double Fraction, double? Tick, string PaceLine, bool HasPace, bool IsOver, Brush Color);

internal sealed record IssueVm(string Message, bool CanConnect);

internal sealed record ChartBarVm(double Height);

internal sealed record CostVm(string Today, string ThirtyDays, string TodayTokens, string ThirtyDayTokens, string MaxText,
                              List<ChartBarVm> Bars, string? TopModel, string Footnote, Brush BarBrush);

internal sealed class AccountVm
{
    public required Guid Id { get; init; }
    public required string Tag { get; init; }
    public required string Name { get; init; }
    public required Brush Badge { get; init; }
    public string? HeaderNote { get; init; }
    public string? CostLine { get; init; }
    public IssueVm? Issue { get; init; }
    public List<LimitVm> Limits { get; init; } = new();
    public string? NoLimits { get; init; }
    public string? ExtraUsage { get; init; }
    public string? ThirtyDays { get; init; }
    public string? Loading { get; init; }
    public double ValuesOpacity { get; init; } = 1;
    public string UpdatedText { get; init; } = "";
    public CostVm? Cost { get; init; }
    public bool Counting { get; init; }
    public bool Selected { get; init; }
}

internal sealed class FlyoutVm
{
    public string UpdatedText { get; init; } = "";
    public List<AccountVm> Accounts { get; init; } = new();
    public AccountVm? Selected { get; init; }
    /// <summary>The empty state, layout A or layout B (with tabs only when there is another account to switch to).</summary>
    public DropdownContent Content { get; init; }
    public bool ShowTabs => Content.Tabs;
    /// <summary>Past four tabs a name would be cut to an ellipsis, so the tabs show badges only (the name is the tooltip).</summary>
    public bool ShowTabNames => Accounts.Count <= 4;

    static Brush Hex(string hex)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch (FormatException) { return Brushes.Gray; }
    }

    public static FlyoutVm Build(AppStore store)
    {
        var now = store.Now;
        var accounts = store.Settings.Accounts.Select(a => Account(store, a, now)).ToList();
        var selectedId = store.SelectedTab is { } id && accounts.Any(x => x.Id == id) ? id : accounts.FirstOrDefault()?.Id;
        accounts = accounts.Select(a => a.Id == selectedId ? Clone(a, true) : a).ToList();
        return new FlyoutVm
        {
            UpdatedText = Formatters.UpdatedAgo(store.LastUpdated, now),
            Accounts = accounts,
            Selected = accounts.FirstOrDefault(a => a.Selected),
            Content = DropdownContent.For(store.Settings.DropdownLayout, accounts.Count),
        };
    }

    static AccountVm Clone(AccountVm a, bool selected) => new()
    {
        Id = a.Id, Tag = a.Tag, Name = a.Name, Badge = a.Badge, HeaderNote = a.HeaderNote, CostLine = a.CostLine, Issue = a.Issue,
        Limits = a.Limits, NoLimits = a.NoLimits, ExtraUsage = a.ExtraUsage, ThirtyDays = a.ThirtyDays, Loading = a.Loading,
        ValuesOpacity = a.ValuesOpacity, UpdatedText = a.UpdatedText, Cost = a.Cost, Counting = a.Counting, Selected = selected,
    };

    static AccountVm Account(AppStore store, Account a, DateTimeOffset now)
    {
        var st = store.State(a);
        var pres = store.Presentation(a);
        // The same shade as the tray icon on a taskbar of the same theme, so the chosen colour looks the same everywhere.
        var color = Hex(a.Preset.For(Theme.AppsDark));
        var greyed = pres.GreysValues || store.IsStale(a);
        var limits = st.Snapshot?.Ordered.Select(l =>
        {
            var d = new LimitDisplay(l, now);
            return new LimitVm(l.Name, d.PercentText, d.CompactTrailing is { } t ? " · " + t : null, $"{l.Name} {d.PercentText} used",
                               d.LargeTrailing, Math.Clamp(l.Percent / 100, 0, 1), d.TickFraction, d.PaceLine ?? " ", d.PaceLine is not null,
                               d.IsOverPace, color);
        }).ToList() ?? new();

        var cost = store.Cost(a);
        string? costLine = cost is null ? null : cost.Summary is { } cs ? "Today " + store.Money(cs.TodayUsd) : "Counting…";
        string? thirty = cost?.Summary is { } s30 ? $"30 days {store.Money(s30.Last30Usd)} · {Formatters.Tokens(s30.Last30Tokens)} tokens" : null;

        CostVm? costVm = null;
        if (cost?.Summary is { } s)
        {
            var max = s.DailyUsd.DefaultIfEmpty(0).Max();
            var bars = s.DailyUsd.Select(v => new ChartBarVm(Math.Max(2, max > 0 ? v / max * 64 : 2))).ToList();
            var foot = "Cost and tokens estimated from this PC's Claude Code logs at API rates.";
            if (s.UnpricedModels.Count > 0)
                foot += $" Unpriced model{(s.UnpricedModels.Count > 1 ? "s" : "")}: {string.Join(", ", s.UnpricedModels)} (counted as {store.Money(0)}).";
            var barBrush = Hex(a.Preset.For(Theme.AppsDark)).Clone();
            barBrush.Opacity = 0.8;
            barBrush.Freeze();
            costVm = new CostVm(store.Money(s.TodayUsd), store.Money(s.Last30Usd), Formatters.Tokens(s.TodayTokens),
                                Formatters.Tokens(s.Last30Tokens), "max " + store.MoneyWhole(max), bars,
                                s.TopModel is { } tm ? "Top model: " + tm : null, foot, barBrush);
        }

        return new AccountVm
        {
            Id = a.Id, Tag = a.Tag, Name = a.Name, Badge = Hex(a.Preset.Badge),
            HeaderNote = pres.HeaderNote,
            CostLine = costLine,
            Issue = pres.InlineIssue is { } issue ? new IssueVm(issue.Message(), issue.Kind == IssueKind.NotConnected) : null,
            Limits = limits,
            NoLimits = st.Snapshot is not null && limits.Count == 0 ? "No limits reported" : null,
            ExtraUsage = st.Snapshot?.Extra is { } e ? Formatters.ExtraUsageLine(e) : null,
            ThirtyDays = thirty,
            Loading = st.Snapshot is null && st.Issue is null ? "Loading…" : null,
            ValuesOpacity = greyed ? 0.5 : 1,
            UpdatedText = Formatters.UpdatedAgo(st.LastSuccess, now),
            Cost = costVm,
            Counting = cost is { Summary: null },
        };
    }
}
