namespace UsageBar.Core;

/// <summary>USD per million tokens.</summary>
public sealed record ModelPrice(double Input, double Output, double CacheWrite5m, double CacheWrite1h, double CacheRead);

public readonly record struct TokenCounts(long Input = 0, long Output = 0, long CacheWrite5m = 0, long CacheWrite1h = 0, long CacheRead = 0)
{
    public long Total => Input + Output + CacheWrite5m + CacheWrite1h + CacheRead;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheWrite5m + b.CacheWrite5m, a.CacheWrite1h + b.CacheWrite1h, a.CacheRead + b.CacheRead);
}

public enum PriceMatchKind { Exact, Family, Unpriced }

public sealed record PriceMatch(PriceMatchKind Kind, string? Key = null)
{
    public static PriceMatch Exact(string key) => new(PriceMatchKind.Exact, key);
    public static PriceMatch Family(string key) => new(PriceMatchKind.Family, key);
    public static readonly PriceMatch Unpriced = new(PriceMatchKind.Unpriced);
}

public static class Pricing
{
    // Source: Anthropic pricing page https://platform.claude.com/docs/en/about-claude/pricing (read 2026-10-25),
    // the same table as macos/Sources/UsageBarCore/Pricing.swift and docs/DATA-SOURCES.md.
    // Haiku 5.5's >100K-prompt tier is deliberately ignored.
    public static readonly IReadOnlyDictionary<string, ModelPrice> Table = new Dictionary<string, ModelPrice>
    {
        ["claude-opus-5-5"] = new(4.00, 20.00, 5.00, 8.00, 0.20),
        ["claude-sonnet-5-5"] = new(2.00, 10.00, 2.50, 4.00, 0.10),
        ["claude-haiku-5-5"] = new(0.10, 0.50, 0.125, 0.20, 0.01),
        ["claude-fable-5-1"] = new(10.00, 50.00, 12.50, 20.00, 0.25),
    };

    /// <summary>Unknown models map by family substring to the current model of that family.</summary>
    static readonly (string Family, string Key)[] Families =
    {
        ("opus", "claude-opus-5-5"), ("sonnet", "claude-sonnet-5-5"), ("haiku", "claude-haiku-5-5"), ("fable", "claude-fable-5-1"),
    };

    public static PriceMatch Match(string model)
    {
        var m = model.ToLowerInvariant();
        if (Table.ContainsKey(m)) return PriceMatch.Exact(m);
        // Suffixed ids of a priced model, e.g. "claude-opus-5-5[1m]" or a dated snapshot.
        if (Table.Keys.OrderByDescending(k => k.Length).FirstOrDefault(k => m.StartsWith(k, StringComparison.Ordinal)) is { } key)
            return PriceMatch.Exact(key);
        foreach (var (family, k) in Families)
            if (m.Contains(family, StringComparison.Ordinal)) return PriceMatch.Family(k);
        return PriceMatch.Unpriced;
    }

    public static ModelPrice? Price(string model) => Match(model) is { Key: { } k } ? Table[k] : null;

    /// <summary>USD cost, or null for an unpriced model.</summary>
    public static double? Cost(TokenCounts c, string model)
    {
        if (Price(model) is not { } p) return null;
        const double perToken = 1.0 / 1_000_000;
        return (c.Input * p.Input + c.Output * p.Output + c.CacheWrite5m * p.CacheWrite5m
                + c.CacheWrite1h * p.CacheWrite1h + c.CacheRead * p.CacheRead) * perToken;
    }
}
