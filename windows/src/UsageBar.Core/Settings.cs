using System.Globalization;
using System.Text.Json;
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

public enum DropdownLayout { BothAccounts, OneAtATime }

public static class EnumTitles
{
    public static string Title(this TrayIconMode m) => m == TrayIconMode.PerAccount ? "One per account" : "Single icon";
    public static string Title(this DropdownLayout l) => l == DropdownLayout.BothAccounts ? "Both accounts" : "One at a time";
}

public sealed record MetricSelection(bool Session = true, bool Weekly = true, bool Scoped = false);

public enum SourceKind { ClaudeCode, ClaudeDesktop, Fixture }

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
    /// <summary>One account by default; the user adds others. Nothing appears without a sign-in behind it.</summary>
    public List<Account> Accounts { get; set; } = new() { Account.DefaultPersonal() };
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

    /// <summary>Bumped when saved settings need a one-time change on load (see <see cref="ApplyMigrations"/>).</summary>
    public int DefaultsVersion { get; set; } = CurrentDefaultsVersion;
    public const int CurrentDefaultsVersion = 1;

    public static readonly int[] RefreshOptions = { 1, 2, 5, 10 };

    [JsonIgnore]
    public MetricSelection Metrics => new(ShowSession, ShowWeekly, ShowScoped);

    /// <summary>Saved form. Fixture accounts (<c>--fixtures</c>) are test data and are never saved.</summary>
    public string ToJson()
    {
        var saved = Accounts;
        Accounts = Accounts.Where(a => a.Source.Kind != SourceKind.Fixture).ToList();
        try { return JsonSerializer.Serialize(this, AppJson.Options); }
        finally { Accounts = saved; }
    }

    /// <summary>
    /// One-time changes for settings saved by older versions. 0.1.0 created a Work account on the Claude app
    /// by default; if the user never touched or connected it, it goes, so only real accounts show.
    /// </summary>
    public bool ApplyMigrations()
    {
        if (DefaultsVersion >= CurrentDefaultsVersion) return false;
        var work = Account.DefaultWork();
        if (!DesktopConnected)
            Accounts.RemoveAll(a => a.Source.Kind == SourceKind.ClaudeDesktop && a.Name == work.Name && a.Tag == work.Tag && a.Colour == work.Colour);
        if (Accounts.Count == 0) Accounts.Add(Account.DefaultPersonal());
        DefaultsVersion = CurrentDefaultsVersion;
        return true;
    }

    public static AppSettings FromJson(string json)
    {
        var s = new AppSettings();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return s; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return s;
            T? Get<T>(string key)
            {
                if (!r.TryGetProperty(key, out var v)) return default;
                try { return v.Deserialize<T>(AppJson.Options); }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return default; }
            }
            if (Get<List<Account>>("accounts") is { Count: > 0 } accounts)
                // 0.1.x colours were free-form hex: keep the nearest preset.
                s.Accounts = accounts.Select(a => a.LegacyColorHex is { } hex
                    ? a with { Colour = AccountColours.Nearest(hex).Id, LegacyColorHex = null }
                    : a with { Colour = AccountColours.Get(a.Colour).Id }).ToList();
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
            // Absent in 0.1.0 files: they need the migrations.
            s.DefaultsVersion = Get<int?>("defaultsVersion") ?? 0;
        }
        s.ApplyMigrations();
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
