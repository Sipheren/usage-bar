using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UsageBar.Core;

public enum LabelStyle { Bars, Percentages, Compact, IconOnly }

public static class LabelStyles
{
    /// <summary>Styles offered in Settings on Windows. Compact reads the same as Percentages in a square icon.</summary>
    public static readonly LabelStyle[] Offered = { LabelStyle.Bars, LabelStyle.Percentages, LabelStyle.IconOnly };

    public static string Title(this LabelStyle s) => s switch
    {
        LabelStyle.Bars => "Bars",
        LabelStyle.Percentages => "Percentages",
        LabelStyle.Compact => "Compact",
        _ => "Icon only",
    };
}

/// <summary>One notification-area icon per shown account (the default), or one icon for all of them.</summary>
public enum TrayIconMode { PerAccount, Single }

/// <summary><c>BothAccounts</c> is the saved name from when there were always two; it shows every account.</summary>
public enum DropdownLayout { BothAccounts, OneAtATime }

public static class EnumTitles
{
    public static string Title(this TrayIconMode m) => m == TrayIconMode.PerAccount ? "One per account" : "Single icon";
    public static string Title(this DropdownLayout l) => l == DropdownLayout.BothAccounts ? "All accounts" : "One at a time";
}

public sealed record MetricSelection(bool Session = true, bool Weekly = true, bool Scoped = false);

public enum SourceKind { ClaudeCode, ClaudeDesktop, Fixture }

public static class AccountSources
{
    /// <summary>The sources Settings > Add Account offers, once each (decision 16). Fixtures are test data only.</summary>
    public static readonly SourceKind[] Addable = { SourceKind.ClaudeCode, SourceKind.ClaudeDesktop };
}

/// <summary>Where an account's sign-in comes from. <c>Dir</c> is the Claude Code config folder or the fixtures folder.</summary>
public sealed record AccountSource(SourceKind Kind, string? Dir = null)
{
    public static AccountSource ClaudeCode(string? configDir = null) => new(SourceKind.ClaudeCode, configDir ?? Paths.DefaultClaudeConfigDir);
    public static readonly AccountSource ClaudeDesktop = new(SourceKind.ClaudeDesktop);
    public static AccountSource Fixture(string dir) => new(SourceKind.Fixture, dir);

    public string Label => Kind switch
    {
        SourceKind.ClaudeCode => "Claude Code login",
        SourceKind.ClaudeDesktop => "Claude app",
        _ => "Test fixtures",
    };
}

public sealed record Account
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Account";
    public string Tag { get; init; } = "A";
    /// <summary>The account's colour preset (<see cref="AccountColours"/>), used in the tray and the flyout alike.</summary>
    public string Colour { get; init; } = "orange";
    /// <summary>0.1.x saved a free-form colour; it's read once, mapped to the nearest preset and not written again.</summary>
    [JsonPropertyName("colorHex")]
    public string? LegacyColorHex { get; init; }
    public bool ShowInTray { get; init; } = true;
    public AccountSource Source { get; init; } = AccountSource.ClaudeCode();
    /// <summary>Claude Code projects folder for the cost estimate; null means no cost figures.</summary>
    public string? LogsDir { get; init; }

    public static Account DefaultPersonal() => new()
    {
        Name = "Personal", Tag = "P", Colour = "orange", Source = AccountSource.ClaudeCode(),
        LogsDir = Path.Combine(Paths.DefaultClaudeConfigDir, "projects"),
    };

    public static Account DefaultClaudeApp() => new()
    {
        Name = "Claude app", Tag = "C", Colour = "purple", Source = AccountSource.ClaudeDesktop,
    };

    /// <summary>The account a first run and Settings > Add Account create for a sign-in source (one per source).</summary>
    public static Account DefaultAccount(SourceKind kind) => kind switch
    {
        SourceKind.ClaudeCode => DefaultPersonal(),
        SourceKind.ClaudeDesktop => DefaultClaudeApp(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "Fixture accounts are test data, not a sign-in source."),
    };

    /// <summary>The demo and fixture Work account (on the Claude app source), as in the Mac screenshots.</summary>
    public static Account DefaultWork() => new()
    {
        Name = "Work", Tag = "W", Colour = "blue", Source = AccountSource.ClaudeDesktop,
    };

    [JsonIgnore]
    public ColourPreset Preset => AccountColours.Get(Colour);
}

public static class Rgb
{
    public static bool TryParse(string hex, out double r, out double g, out double b)
    {
        r = g = b = 0;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        r = ((v >> 16) & 0xFF) / 255.0;
        g = ((v >> 8) & 0xFF) / 255.0;
        b = (v & 0xFF) / 255.0;
        return true;
    }

    public static string Hex(double r, double g, double b)
    {
        static int C(double x) => (int)Formatters.Round(Math.Clamp(x, 0, 1) * 255);
        return $"#{C(r):X2}{C(g):X2}{C(b):X2}";
    }
}

/// <summary>Everything the user can set. Every field is optional when loading, so older files never reset everything.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// None by default: a first run adds one per sign-in it finds (<see cref="FirstRun"/>), and the user adds the rest.
    /// Zero accounts is a normal state (decision 16).
    /// </summary>
    public List<Account> Accounts { get; set; } = new();
    public LabelStyle LabelStyle { get; set; } = LabelStyle.Bars;
    public TrayIconMode TrayIcons { get; set; } = TrayIconMode.PerAccount;
    public bool ShowSession { get; set; } = true;
    public bool ShowWeekly { get; set; } = true;
    public bool ShowScoped { get; set; }
    public DropdownLayout DropdownLayout { get; set; } = DropdownLayout.BothAccounts;
    public int RefreshMinutes { get; set; } = 2;
    /// <summary>ISO code of the currency costs are shown in. USD means no conversion and no rate fetch.</summary>
    public string Currency { get; set; } = "AUD";
    /// <summary>Set once the user clicks Connect… for the Claude app source (decision 12).</summary>
    public bool DesktopConnected { get; set; }
    /// <summary>Token-cache entry chosen per Claude app account uuid (a key path, never a token).</summary>
    public Dictionary<string, string> DesktopTokenChoice { get; set; } = new();

    public static readonly int[] RefreshOptions = { 1, 2, 5, 10 };

    [JsonIgnore]
    public MetricSelection Metrics => new(ShowSession, ShowWeekly, ShowScoped);

    /// <summary>
    /// Saved accounts this build couldn't read (a source kind from a newer build, a bad id), kept as JSON and written
    /// back unchanged so a build that understands them still has them.
    /// </summary>
    [JsonIgnore]
    public List<string> UnreadAccounts { get; set; } = new();

    /// <summary>Saved form. Fixture accounts (<c>--fixtures</c>) are test data and are never saved.</summary>
    public string ToJson()
    {
        var saved = Accounts;
        Accounts = Accounts.Where(a => a.Source?.Kind != SourceKind.Fixture).ToList();
        try
        {
            if (UnreadAccounts.Count == 0) return JsonSerializer.Serialize(this, AppJson.Options);
            var node = JsonSerializer.SerializeToNode(this, AppJson.Options)!;
            var list = node["accounts"]!.AsArray();
            foreach (var raw in UnreadAccounts) list.Add(JsonNode.Parse(raw));
            return node.ToJsonString(AppJson.Options);
        }
        finally { Accounts = saved; }
    }

    /// <summary>One saved account, or null when this build can't use it. Missing names and tags get defaults.</summary>
    static Account? ReadAccount(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        Account? a;
        try { a = e.Deserialize<Account>(AppJson.Options); }
        catch (Exception x) when (x is JsonException or NotSupportedException or InvalidOperationException or FormatException) { return null; }
        if (a?.Source is null || !Enum.IsDefined(a.Source.Kind) || !e.TryGetProperty("id", out _) || !e.TryGetProperty("source", out _)) return null;
        var fallback = new Account();
        return a with
        {
            Name = string.IsNullOrEmpty(a.Name) ? fallback.Name : a.Name,
            Tag = string.IsNullOrEmpty(a.Tag) ? fallback.Tag : a.Tag,
            // 0.1.x colours were free-form hex: keep the nearest preset.
            Colour = a.LegacyColorHex is { } hex ? AccountColours.Nearest(hex).Id : AccountColours.Get(a.Colour ?? "").Id,
            LegacyColorHex = null,
        };
    }

    /// <summary>Settings for a fresh install: one account per sign-in found, none when nothing was found.</summary>
    public static AppSettings FirstRun(FoundSignIns found) => new() { Accounts = found.Kinds.Select(Account.DefaultAccount).ToList() };

    /// <summary>
    /// Saved settings, loaded as they are: nothing adds, removes or renames accounts (decision 16). Null when the
    /// text isn't a JSON object, which the app treats as a first run.
    /// </summary>
    public static AppSettings? FromJson(string json)
    {
        var s = new AppSettings();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            T? Get<T>(string key)
            {
                if (!r.TryGetProperty(key, out var v)) return default;
                try { return v.Deserialize<T>(AppJson.Options); }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return default; }
            }
            // Any saved list, an empty one included, is kept, each account on its own: one this build can't read
            // is set aside and written back, never allowed to take the others with it. Every Windows version
            // saved its accounts, so only a file without a list falls back to the account all of them started with.
            if (r.TryGetProperty("accounts", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Null) continue;
                    var a = ReadAccount(e);
                    if (a is null) s.UnreadAccounts.Add(e.GetRawText());
                    else if (s.Accounts.All(x => x.Id != a.Id)) s.Accounts.Add(a);   // a duplicated id is dropped
                }
            }
            else s.Accounts = new() { Account.DefaultPersonal() };
            if (Get<LabelStyle?>("labelStyle") is { } ls) s.LabelStyle = ls;
            if (Get<TrayIconMode?>("trayIcons") is { } ti) s.TrayIcons = ti;
            if (Get<bool?>("showSession") is { } ss) s.ShowSession = ss;
            if (Get<bool?>("showWeekly") is { } sw) s.ShowWeekly = sw;
            if (Get<bool?>("showScoped") is { } sc) s.ShowScoped = sc;
            if (Get<DropdownLayout?>("dropdownLayout") is { } dl) s.DropdownLayout = dl;
            if (Get<int?>("refreshMinutes") is { } rm && RefreshOptions.Contains(rm)) s.RefreshMinutes = rm;
            if (Get<string>("currency") is { Length: > 0 } cur) s.Currency = cur.ToUpperInvariant();
            if (Get<bool?>("desktopConnected") is { } dc) s.DesktopConnected = dc;
            if (Get<Dictionary<string, string>>("desktopTokenChoice") is { } tc) s.DesktopTokenChoice = tc;
        }
        return s;
    }
}

public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
}
