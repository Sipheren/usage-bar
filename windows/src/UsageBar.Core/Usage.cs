using System.Globalization;
using System.Text.Json;

namespace UsageBar.Core;

// MARK: Normalised model

public enum LimitKindType { Session, Weekly, Scoped }

/// <summary>Session, weekly, or a model-scoped weekly limit. <c>Id</c> tells two scoped limits apart; <c>Name</c> is shown.</summary>
public sealed record LimitKind(LimitKindType Type, string? Id = null, string? Name = null)
{
    public static readonly LimitKind Session = new(LimitKindType.Session);
    public static readonly LimitKind Weekly = new(LimitKindType.Weekly);
    public static LimitKind Scoped(string id, string name) => new(LimitKindType.Scoped, id, name);
}

public sealed record UsageLimit(LimitKind Kind, double Percent, DateTimeOffset? ResetsAt,
                                string? Severity = null, bool? IsActive = null)
{
    public string Id => Kind.Type switch
    {
        LimitKindType.Session => "session",
        LimitKindType.Weekly => "weekly",
        _ => "scoped:" + Kind.Id,
    };

    public string Name => Kind.Type switch
    {
        LimitKindType.Session => "Session",
        LimitKindType.Weekly => "Weekly",
        _ => Kind.Name + " weekly",
    };

    /// <summary>Window length in minutes: session 300, weekly (all or scoped) 10080.</summary>
    public double WindowMinutes => Kind.Type == LimitKindType.Session ? 300 : 10080;

    public bool IsScoped => Kind.Type == LimitKindType.Scoped;
}

public sealed record ExtraUsage(bool? IsEnabled, double? MonthlyLimit, double? UsedCredits, double? Utilization,
                                string? Currency, int? DecimalPlaces, string? DisabledReason);

public sealed class UsageSnapshot : IEquatable<UsageSnapshot>
{
    public List<UsageLimit> Limits { get; init; } = new();
    public ExtraUsage? Extra { get; init; }
    public DateTimeOffset FetchedAt { get; init; }

    public UsageSnapshot() { }

    public UsageSnapshot(List<UsageLimit> limits, ExtraUsage? extra, DateTimeOffset fetchedAt)
    {
        Limits = limits;
        Extra = extra;
        FetchedAt = fetchedAt;
    }

    public UsageLimit? Session => Limits.FirstOrDefault(l => l.Kind.Type == LimitKindType.Session);
    public UsageLimit? Weekly => Limits.FirstOrDefault(l => l.Kind.Type == LimitKindType.Weekly);
    public List<UsageLimit> Scoped => Limits.Where(l => l.IsScoped).ToList();

    /// <summary>Display order: session, weekly, then scoped limits.</summary>
    public List<UsageLimit> Ordered
    {
        get
        {
            var out_ = new List<UsageLimit>();
            if (Session is { } s) out_.Add(s);
            if (Weekly is { } w) out_.Add(w);
            out_.AddRange(Scoped);
            return out_;
        }
    }

    public bool Equals(UsageSnapshot? other) =>
        other is not null && Limits.SequenceEqual(other.Limits) && Equals(Extra, other.Extra) && FetchedAt == other.FetchedAt;

    public override bool Equals(object? obj) => Equals(obj as UsageSnapshot);
    public override int GetHashCode() => HashCode.Combine(Limits.Count, Extra, FetchedAt);
}

// MARK: Decoding

public static class UsageDecoder
{
    public sealed class DecodeException : Exception
    {
        public DecodeException() : base("not a usage object") { }
    }

    /// <summary>
    /// Decodes <c>/api/oauth/usage</c> (or the claude.ai org usage endpoint). Unknown keys and nulls are ignored.
    /// Prefers <c>limits[]</c>; falls back to <c>five_hour</c> / <c>seven_day</c> when it is absent or yields nothing.
    /// </summary>
    public static UsageSnapshot Decode(ReadOnlySpan<byte> data, DateTimeOffset fetchedAt)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data.ToArray()); }
        catch (JsonException) { throw new DecodeException(); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DecodeException();

            var limits = new List<UsageLimit>();
            if (root.TryGetProperty("limits", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in arr.EnumerateArray())
                {
                    // One malformed entry never sinks the response.
                    if (l.ValueKind != JsonValueKind.Object) continue;
                    var pct = Json.Number(l, "percent") ?? Json.Number(l, "utilization");
                    if (pct is null) continue;
                    var resets = Json.String(l, "resets_at") is { } r ? IsoDate.Parse(r) : null;
                    var model = Json.Object(Json.Object(l, "scope"), "model");
                    var modelId = Json.String(model, "id");
                    var scopeName = Json.String(model, "display_name") ?? modelId;
                    // Identity falls back to the name when there's no id; two scoped limits with distinct model
                    // ids both show even if they share a display name.
                    LimitKind Scoped() => LimitKind.Scoped(modelId ?? scopeName ?? "scoped", scopeName ?? "Scoped");
                    var group = Json.String(l, "group");
                    LimitKind? kind = Json.String(l, "kind") switch
                    {
                        "session" => LimitKind.Session,
                        "weekly_all" => LimitKind.Weekly,
                        "weekly_scoped" => Scoped(),
                        _ => group == "session" ? LimitKind.Session
                            : group == "weekly" ? (scopeName is not null ? Scoped() : LimitKind.Weekly)
                            : null,
                    };
                    if (kind is null || limits.Any(x => x.Kind == kind)) continue;
                    limits.Add(new UsageLimit(kind, pct.Value, resets, Json.String(l, "severity"), Json.Bool(l, "is_active")));
                }
            }

            if (limits.Count == 0)
            {
                var f = Json.Object(root, "five_hour");
                if (Json.Number(f, "utilization") is { } fu)
                    limits.Add(new UsageLimit(LimitKind.Session, fu, Json.String(f, "resets_at") is { } fr ? IsoDate.Parse(fr) : null));
                var s = Json.Object(root, "seven_day");
                if (Json.Number(s, "utilization") is { } su)
                    limits.Add(new UsageLimit(LimitKind.Weekly, su, Json.String(s, "resets_at") is { } sr ? IsoDate.Parse(sr) : null));
            }

            ExtraUsage? extra = null;
            if (Json.Object(root, "extra_usage") is { } e)
            {
                extra = new ExtraUsage(Json.Bool(e, "is_enabled"), Json.Number(e, "monthly_limit"), Json.Number(e, "used_credits"),
                                       Json.Number(e, "utilization"), Json.String(e, "currency"),
                                       Json.Number(e, "decimal_places") is { } dp ? (int)dp : null,
                                       Json.String(e, "disabled_reason"));
            }
            return new UsageSnapshot(limits, extra, fetchedAt);
        }
    }
}

/// <summary>Lenient accessors: a wrong type reads as missing, never as an error.</summary>
internal static class Json
{
    public static JsonElement? Object(JsonElement? parent, string key) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : null;

    public static string? String(JsonElement? parent, string key) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static bool? Bool(JsonElement? parent, string key) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(key, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static double? Number(JsonElement? parent, string key)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }
}

// MARK: Profile

public sealed record Profile(string? Email = null, string? DisplayName = null, string? AccountUuid = null,
                             string? OrganizationUuid = null, string? OrganizationName = null,
                             string? OrganizationType = null, string? RateLimitTier = null)
{
    public static Profile? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var account = Json.Object(root, "account");
            var org = Json.Object(root, "organization");
            return new Profile(Json.String(account, "email") ?? Json.String(account, "email_address"),
                               Json.String(account, "display_name"), Json.String(account, "uuid"),
                               Json.String(org, "uuid"), Json.String(org, "name"),
                               Json.String(org, "organization_type"), Json.String(org, "rate_limit_tier"));
        }
        catch (JsonException) { return null; }
    }
}

// MARK: Dates

public static class IsoDate
{
    /// <summary>Parses ISO 8601 with or without fractional seconds (any number of digits) and an offset or Z.</summary>
    public static DateTimeOffset? Parse(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 20 || s[10] != 'T') return null;
        // DateTimeOffset keeps at most 7 fraction digits; trim longer fractions before parsing.
        var dot = s.IndexOf('.', 19);
        if (dot == 19)
        {
            var end = dot + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
            if (end - dot - 1 > 7) s = s[..(dot + 8)] + s[end..];
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;
    }
}
