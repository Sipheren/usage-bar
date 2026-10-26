namespace UsageBar.Core;

public enum PaceStatus { OnPace, Under, Over }

public sealed record Pace(double Expected, double Delta, double ElapsedMinutes, PaceStatus Status, DateTimeOffset? RunOut, double Used)
{
    /// <summary>|delta| at or below this many points counts as on pace.</summary>
    public const double OnPaceBand = 2.0;

    public static Pace Compute(double used, DateTimeOffset resetsAt, double windowMinutes, DateTimeOffset now)
    {
        var remaining = (resetsAt - now).TotalSeconds / 60;
        var elapsed = Math.Min(Math.Max(windowMinutes - remaining, 0), windowMinutes);
        var expected = windowMinutes > 0 ? elapsed / windowMinutes * 100 : 0;
        var delta = used - expected;

        // Band on the rounded figure the text shows, so "2% under pace" never appears.
        var status = Formatters.Round(Math.Abs(delta)) <= OnPaceBand ? PaceStatus.OnPace
            : delta < 0 ? PaceStatus.Under : PaceStatus.Over;

        DateTimeOffset? runOut = null;
        if (status == PaceStatus.Over)
        {
            if (used >= 100) runOut = now;
            else if (used > 0)
            {
                var rate = used / Math.Max(elapsed, 1);          // percent per minute so far
                var minutesToFull = (100 - used) / rate;
                var candidate = now.AddTicks((long)(minutesToFull * 60 * TimeSpan.TicksPerSecond));
                if (candidate < resetsAt) runOut = candidate;
            }
        }
        return new Pace(expected, delta, elapsed, status, runOut, used);
    }

    /// <summary>Pace marker position on the bar, 0–1.</summary>
    public double TickFraction => Math.Min(Math.Max(Expected / 100, 0), 1);

    public int Points => (int)Formatters.Round(Math.Abs(Delta));

    public bool IsOver => Status == PaceStatus.Over;

    public string Line(DateTimeOffset now, TimeZoneInfo? timeZone = null) => Status switch
    {
        PaceStatus.OnPace => "On pace · lasts until reset",
        PaceStatus.Under => $"{Points}% under pace · lasts until reset",
        _ when Used >= 100 => $"{Points}% over pace · limit reached",
        _ when RunOut is { } r => $"{Points}% over pace · runs out around {Formatters.RunOutTime(r, now, timeZone)}",
        _ => $"{Points}% over pace · lasts until reset",
    };
}

/// <summary>Everything the flyout shows for one limit row, in one place so both layouts agree.</summary>
public sealed class LimitDisplay
{
    public string PercentText { get; }
    /// <summary>Layout A, after the bold percent: "resets in 2h 10m", "starts on first use", or null.</summary>
    public string? CompactTrailing { get; }
    /// <summary>Layout B, right-hand side: "Resets in 2h 10m", "Starts on first use", or null.</summary>
    public string? LargeTrailing { get; }
    /// <summary>Pace sentence; null when the window hasn't started, so no pace can be computed.</summary>
    public string? PaceLine { get; }
    /// <summary>Pace marker on the bar, 0–1; null when there's no pace.</summary>
    public double? TickFraction { get; }
    public bool IsOverPace { get; }

    public LimitDisplay(UsageLimit limit, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var pct = (int)Formatters.Round(Math.Max(0, limit.Percent));
        PercentText = $"{pct}%";
        if (limit.ResetsAt is { } reset)
        {
            var phrase = Formatters.ResetPhrase(reset, now, timeZone);
            CompactTrailing = "resets " + phrase;
            LargeTrailing = "Resets " + phrase;
            var pace = Pace.Compute(limit.Percent, reset, limit.WindowMinutes, now);
            PaceLine = pace.Line(now, timeZone);
            TickFraction = pace.TickFraction;
            IsOverPace = pace.IsOver;
        }
        else
        {
            // No reset time: the window starts with the first request. Nothing to pace against.
            CompactTrailing = pct == 0 ? "starts on first use" : null;
            LargeTrailing = pct == 0 ? "Starts on first use" : null;
        }
    }
}
