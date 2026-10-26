using System.Text.Json;

namespace UsageBar.Core;

/// <summary>
/// Display currency for cost figures. Costs are computed in US dollars and converted with the daily rate;
/// with USD chosen nothing is converted and no rate is ever fetched.
/// </summary>
public static class Currency
{
    /// <summary>Offered in Settings, in this order. The locale's currency joins them when the rate list has it.</summary>
    public static readonly string[] BuiltIn = { "AUD", "USD", "EUR", "GBP", "NZD", "CAD", "JPY" };

    public static string[] Options(string? localeCode, IReadOnlySet<string> available)
    {
        var code = localeCode?.ToUpperInvariant();
        if (code is null || BuiltIn.Contains(code) || !available.Contains(code)) return BuiltIn;
        return BuiltIn.Append(code).ToArray();
    }

    public static bool NeedsRate(string code) => code.ToUpperInvariant() != "USD";

    /// <summary>US-dollar amount in <paramref name="code"/>, or null when that currency's rate isn't known yet.</summary>
    public static double? Convert(double usd, string code, ExchangeRates rates)
    {
        if (!NeedsRate(code)) return usd;
        return rates.Rates.TryGetValue(code.ToUpperInvariant(), out var r) ? usd * r : null;
    }

    public static string Symbol(string code) => code.ToUpperInvariant() switch
    {
        "AUD" => "A$",
        "USD" => "US$",
        "EUR" => "€",
        "GBP" => "£",
        "NZD" => "NZ$",
        "CAD" => "C$",
        "JPY" => "¥",
        var c => c + " ",
    };

    /// <summary>Currencies with no minor unit never show decimals.</summary>
    public static bool HasDecimals(string code) => !new[] { "JPY", "KRW", "VND", "ISK", "CLP" }.Contains(code.ToUpperInvariant());

    /// <summary>"A$6.20" below 100, "A$1,050" from 100 up; "¥938" always whole.</summary>
    public static string Format(double amount, string code) =>
        Symbol(code) + Formatters.Number(amount, HasDecimals(code) && Math.Abs(amount) < 100 ? 2 : 0);

    /// <summary>Whole units, for chart labels: "A$25", "¥3,725".</summary>
    public static string FormatWhole(double amount, string code) => Symbol(code) + Formatters.Number(amount, 0);

    public static string DisplayName(string code) => code.ToUpperInvariant() switch
    {
        "AUD" => "Australian Dollar (AUD)",
        "USD" => "US Dollar (USD)",
        "EUR" => "Euro (EUR)",
        "GBP" => "British Pound (GBP)",
        "NZD" => "New Zealand Dollar (NZD)",
        "CAD" => "Canadian Dollar (CAD)",
        "JPY" => "Japanese Yen (JPY)",
        var c => c,
    };
}

/// <summary>US-dollar exchange rates from open.er-api.com (units of each currency per US$1).</summary>
public sealed record ExchangeRates(Dictionary<string, double> Rates, DateTimeOffset AsOf, DateTimeOffset FetchedAt)
{
    public static readonly Uri Url = new("https://open.er-api.com/v6/latest/USD");

    /// <summary>Used until the first fetch: AUD only (the original default currency), rate of 2026-10-25.</summary>
    public static readonly ExchangeRates Fallback = new(new Dictionary<string, double> { ["USD"] = 1, ["AUD"] = 1.423 },
                                                        DateTimeOffset.Parse("2026-10-25T00:00:00Z"), DateTimeOffset.MinValue);

    public bool IsFallback => FetchedAt == DateTimeOffset.MinValue;

    public bool NeedsRefresh(DateTimeOffset now) => (now - FetchedAt).TotalHours > 24;

    /// <summary>Parses the open.er-api.com response; null when it has no usable rates.</summary>
    public static ExchangeRates? Parse(ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray());
            if (Json.Object(doc.RootElement, "rates") is not { } raw) return null;
            var rates = new Dictionary<string, double>();
            foreach (var p in raw.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.GetDouble() > 0) rates[p.Name.ToUpperInvariant()] = p.Value.GetDouble();
            if (rates.Count == 0) return null;
            rates["USD"] = 1;
            var asOf = Json.Number(doc.RootElement, "time_last_update_unix") is { } t
                ? DateTimeOffset.FromUnixTimeSeconds((long)t) : now;
            return new ExchangeRates(rates, asOf, now);
        }
        catch (JsonException) { return null; }
    }
}
