namespace UsageBar.Core;

/// <summary>What one account contributes to the tray.</summary>
public sealed record TrayInput(string Tag, string Name, UsageSnapshot? Snapshot, DateTimeOffset? LastSuccess);

public sealed record TrayAccountItem(string Tag, string Name, List<int> Values, bool Missing, bool Stale,
                                     int? SessionPercent = null, int? WeeklyPercent = null);

/// <summary>
/// The tray icon content, the Windows counterpart of the Mac menu bar label. Always monochrome: the percentages never
/// change its colour (docs/DECISIONS.md, decision 4). <see cref="PlainText"/> matches the Mac label's plain text.
/// </summary>
public sealed class TrayModel
{
    /// <summary>NOTIFYICONDATA.szTip holds 128 characters including the terminator.</summary>
    public const int MaxTooltip = 127;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public LabelStyle Style { get; }
    public List<TrayAccountItem> Accounts { get; }

    TrayModel(LabelStyle style, List<TrayAccountItem> accounts) { Style = style; Accounts = accounts; }

    public static TrayModel Build(IEnumerable<TrayInput> inputs, LabelStyle style, MetricSelection metrics, DateTimeOffset now)
    {
        var items = new List<TrayAccountItem>();
        foreach (var input in inputs)
        {
            var values = new List<int>();
            if (input.Snapshot is { } snap)
            {
                var picked = new List<UsageLimit>();
                if (metrics.Session && snap.Session is { } s) picked.Add(s);
                if (metrics.Weekly && snap.Weekly is { } w) picked.Add(w);
                // Several scoped limits collapse to the highest one: the icon has room for one.
                if (metrics.Scoped && snap.Scoped.OrderByDescending(l => l.Percent).FirstOrDefault() is { } sc) picked.Add(sc);
                values = picked.Select(l => (int)Formatters.Round(Math.Max(0, l.Percent))).ToList();
            }
            var stale = input.LastSuccess is { } last && now - last > StaleAfter;
            int? Pct(UsageLimit? l) => l is null ? null : (int)Formatters.Round(Math.Max(0, l.Percent));
            items.Add(new TrayAccountItem(input.Tag, input.Name, values, values.Count == 0, input.Snapshot is not null && stale,
                                          Pct(input.Snapshot?.Session), Pct(input.Snapshot?.Weekly)));
        }
        return new TrayModel(style, items);
    }

    /// <summary>Percentages spaces the slash ("20% / 60%"); compact uses a tight middle dot ("20·60").</summary>
    public string Separator => Style switch
    {
        LabelStyle.Percentages => " / ",
        LabelStyle.Compact => "·",
        _ => "/",
    };

    /// <summary>Plain-text form, as on the Mac: "P 20% / 60%  W 45% / 72%".</summary>
    public string PlainText => Style switch
    {
        LabelStyle.IconOnly => "✳",
        LabelStyle.Bars => string.Join("  ", Accounts.Select(a =>
            a.Missing ? $"{a.Tag} –" : $"{a.Tag} [" + string.Join("|", a.Values) + "]")),
        _ => string.Join("  ", Accounts.Select(a =>
            a.Missing ? $"{a.Tag} –"
                : $"{a.Tag} " + string.Join(Separator, a.Values.Select(v => Style == LabelStyle.Percentages ? $"{v}%" : $"{v}")))),
    };

    /// <summary>Tooltip for the single icon: every account's session and weekly.</summary>
    public string SingleTooltip()
    {
        var lines = new List<string> { "Usage Bar" };
        foreach (var a in Accounts)
        {
            var parts = new List<string>();
            if (a.SessionPercent is { } s) parts.Add($"session {s}%");
            if (a.WeeklyPercent is { } w) parts.Add($"weekly {w}%");
            lines.Add(parts.Count == 0 ? $"{a.Name} — no data" : $"{a.Name} — " + string.Join(", ", parts));
        }
        return Fit(lines);
    }

    /// <summary>Tooltip for one account's own icon: each limit with its reset.</summary>
    public static string AccountTooltip(string name, UsageSnapshot? snapshot, DateTimeOffset now, TimeZoneInfo? tz = null)
    {
        var lines = new List<string> { "Usage Bar · " + name };
        if (snapshot is null || snapshot.Ordered.Count == 0) lines.Add("No data yet");
        else
            foreach (var l in snapshot.Ordered)
            {
                var d = new LimitDisplay(l, now, tz);
                lines.Add(d.CompactTrailing is { } t ? $"{l.Name} {d.PercentText} · {t}" : $"{l.Name} {d.PercentText}");
            }
        return Fit(lines);
    }

    /// <summary>Joins lines, dropping trailing lines and then truncating so the result fits the shell's limit.</summary>
    static string Fit(List<string> lines)
    {
        while (lines.Count > 1 && string.Join("\n", lines).Length > MaxTooltip) lines.RemoveAt(lines.Count - 1);
        var s = string.Join("\n", lines);
        return s.Length <= MaxTooltip ? s : s[..(MaxTooltip - 1)] + "…";
    }
}
