using System.Globalization;

namespace UsageBar.Core;

public static class Formatters
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Swift's <c>rounded()</c>: to nearest, halves away from zero (.NET's default is to even).</summary>
    public static double Round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>A number with grouping, rounded half to even like the Mac's NumberFormatter: "1,050", "12.50".</summary>
    public static string Number(double v, int digits)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return v.ToString(Inv);
        var d = Math.Round((decimal)v, digits, MidpointRounding.ToEven);
        if (d == 0) d = 0m;   // no "-0.00"
        return d.ToString("N" + digits, Inv);
    }

    static DateTimeOffset InZone(DateTimeOffset d, TimeZoneInfo? tz) => TimeZoneInfo.ConvertTime(d, tz ?? TimeZoneInfo.Local);

    static string AmPm(DateTimeOffset local) => local.Hour < 12 ? "am" : "pm";

    /// <summary>"in 2h 10m" under 24 h, otherwise "Thu 9:00 am" in the given time zone. Callers prefix "Resets"/"resets".</summary>
    public static string ResetPhrase(DateTimeOffset resetsAt, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var seconds = (resetsAt - now).TotalSeconds;
        if (seconds <= 0) return "now";
        if (seconds < 24 * 3600)
        {
            var totalMinutes = (int)Math.Ceiling(seconds / 60);
            int h = totalMinutes / 60, m = totalMinutes % 60;
            if (h == 0) return $"in {m}m";
            if (m == 0) return $"in {h}h";
            return $"in {h}h {m}m";
        }
        // The API sends resets a fraction of a second early (14:59:59.6Z for 15:00Z); round, don't truncate.
        var nearest = RoundTo(resetsAt, 60);
        var local = InZone(nearest, timeZone);
        return local.ToString("ddd h:mm", Inv) + " " + AmPm(local);
    }

    /// <summary>Run-out estimate, rounded to 10 minutes: "3:40 pm" today, "Wed 7 pm" on another day.</summary>
    public static string RunOutTime(DateTimeOffset date, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var local = InZone(RoundTo(date, 600), timeZone);
        // "%h": a lone "h" would be read as a standard format string.
        var time = local.Minute == 0 ? local.ToString("%h", Inv) : local.ToString("h:mm", Inv);
        time += " " + AmPm(local);
        var sameDay = local.Date == InZone(now, timeZone).Date;
        return sameDay ? time : local.ToString("ddd", Inv) + " " + time;
    }

    /// <summary>Rounds to the nearest multiple of <paramref name="seconds"/> since 1970, halves away from zero.</summary>
    public static DateTimeOffset RoundTo(DateTimeOffset d, double seconds)
    {
        var epoch = d.ToUnixTimeMilliseconds() / 1000.0 + (d.Ticks % TimeSpan.TicksPerMillisecond) / 1e7;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(Round(epoch / seconds) * seconds * 1000));
    }

    /// <summary>"6:40 am".</summary>
    public static string ClockTime(DateTimeOffset date, TimeZoneInfo? timeZone = null)
    {
        var local = InZone(date, timeZone);
        return local.ToString("h:mm", Inv) + " " + AmPm(local);
    }

    /// <summary>"Updated just now", "Updated 2 min ago", "Updated 3 h ago".</summary>
    public static string UpdatedAgo(DateTimeOffset? date, DateTimeOffset now)
    {
        if (date is null) return "Not updated yet";
        var minutes = (int)(Math.Max(0, (now - date.Value).TotalSeconds) / 60);
        if (minutes < 1) return "Updated just now";
        if (minutes < 60) return $"Updated {minutes} min ago";
        var hours = minutes / 60;
        if (hours < 48) return $"Updated {hours} h ago";
        return $"Updated {hours / 24} days ago";
    }

    /// <summary>Cost in the display currency: "A$6.20" below 100, "A$1,050" from 100 up.</summary>
    public static string Money(double amount, string symbol = "A$") =>
        symbol + Number(amount, Math.Abs(amount) < 100 ? 2 : 0);

    /// <summary>Whole amounts drop the decimals ("A$50"), others keep them ("A$12.50").</summary>
    public static string MinorUnits(double minor, int decimalPlaces, string? currency)
    {
        var value = minor / Math.Pow(10, Math.Max(0, decimalPlaces));
        var whole = Round(value) == value;
        return CurrencySymbol(currency) + Number(value, whole ? 0 : Math.Max(2, decimalPlaces));
    }

    public static string CurrencySymbol(string? code) => (code ?? "").ToUpperInvariant() switch
    {
        "AUD" => "A$",
        "USD" => "US$",
        "EUR" => "€",
        "GBP" => "£",
        "NZD" => "NZ$",
        "CAD" => "C$",
        "" => "$",
        var c => c + " ",
    };

    /// <summary>"Extra usage A$12.50 of A$50 · off (out of credits)".</summary>
    public static string? ExtraUsageLine(ExtraUsage e)
    {
        if (e.UsedCredits is not { } used) return null;
        var dp = e.DecimalPlaces ?? 2;
        var s = "Extra usage " + MinorUnits(used, dp, e.Currency);
        if (e.MonthlyLimit is { } limit) s += " of " + MinorUnits(limit, dp, e.Currency);
        if (e.IsEnabled == false)
            s += string.IsNullOrEmpty(e.DisabledReason) ? " · off" : " · off (" + e.DisabledReason.Replace('_', ' ') + ")";
        return s;
    }

    /// <summary>"260M", "1.4B", "9.2M", "86M", "12K".</summary>
    public static string Tokens(long n)
    {
        double v = n;
        static string Fmt(double x, string unit)
        {
            if (x < 10)
            {
                var one = Round(x * 10) / 10;
                return (one == Round(one) ? ((long)one).ToString(Inv) : one.ToString("0.0", Inv)) + unit;
            }
            return ((long)Round(x)).ToString(Inv) + unit;
        }
        if (v >= 1e9) return Fmt(v / 1e9, "B");
        if (v >= 1e6) return Fmt(v / 1e6, "M");
        if (v >= 1e3) return Fmt(v / 1e3, "K");
        return n.ToString(Inv);
    }

    /// <summary>"25 Oct 2026".</summary>
    public static string ShortDate(DateTimeOffset d, TimeZoneInfo? timeZone = null) =>
        InZone(d, timeZone).ToString("d MMM yyyy", Inv);

    /// <summary>"US$1 = A$1.42 · updated 26 Oct", "… · built-in rate" before the first fetch, or null for USD.</summary>
    public static string? RateLine(ExchangeRates rates, string code, TimeZoneInfo? timeZone = null)
    {
        if (!Currency.NeedsRate(code)) return null;
        if (!rates.Rates.TryGetValue(code.ToUpperInvariant(), out var rate)) return "Waiting for the exchange rate";
        var b = "US$1 = " + Currency.Symbol(code) + Number(rate, Currency.HasDecimals(code) ? 2 : 0);
        if (rates.IsFallback) return b + " · built-in rate";
        return b + " · updated " + InZone(rates.AsOf, timeZone).ToString("d MMM", Inv);
    }
}
