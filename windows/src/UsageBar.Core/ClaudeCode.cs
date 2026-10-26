using System.Text;
using System.Text.Json;

namespace UsageBar.Core;

public enum CredentialError { NotFound, Malformed, NoToken, Denied, DecryptFailed, Unsupported }

public sealed class CredentialException(CredentialError error, string? detail = null) : Exception(detail ?? error.ToString())
{
    public CredentialError Error { get; } = error;
}

public sealed record CliToken(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;

    // Never let the token reach a log or debugger string.
    public override string ToString() => $"CliToken(expires {ExpiresAt:u})";
}

/// <summary>
/// Reads Claude Code's login from <c>.credentials.json</c> in its config folder (on Windows Claude Code keeps it
/// there, not in a keychain). Read-only: never writes or refreshes it.
/// </summary>
public static class ClaudeCodeCredentials
{
    public const string FileName = ".credentials.json";

    public static CliToken Read(string configDir)
    {
        var path = Path.Combine(Paths.Expand(configDir), FileName);
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { throw new CredentialException(CredentialError.NotFound); }
        catch (DirectoryNotFoundException) { throw new CredentialException(CredentialError.NotFound); }
        catch (UnauthorizedAccessException) { throw new CredentialException(CredentialError.Denied); }
        return Parse(data);
    }

    public static CliToken Parse(byte[] raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripBom(raw));
            var oauth = Json.Object(doc.RootElement, "claudeAiOauth") ?? throw new CredentialException(CredentialError.Malformed);
            var token = Json.String(oauth, "accessToken");
            if (string.IsNullOrEmpty(token)) throw new CredentialException(CredentialError.NoToken);
            DateTimeOffset? expires = Json.Number(oauth, "expiresAt") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;
            return new CliToken(token, expires, Json.String(oauth, "subscriptionType"));
        }
        catch (JsonException) { throw new CredentialException(CredentialError.Malformed); }
    }

    internal static byte[] StripBom(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b[3..] : b;
}

/// <summary>The CLI's account details from <c>~/.claude.json</c>: shown in Settings and used to tell Personal from Work.</summary>
public sealed record ClaudeCodeAccountInfo(string? Email, string? AccountUuid, string? OrganizationUuid, string? OrganizationName)
{
    public static ClaudeCodeAccountInfo? Read(string? path = null)
    {
        try { return Parse(File.ReadAllBytes(path ?? Paths.Expand("~/.claude.json"))); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static ClaudeCodeAccountInfo? Parse(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(ClaudeCodeCredentials.StripBom(data));
            if (Json.Object(doc.RootElement, "oauthAccount") is not { } oa) return null;
            return new ClaudeCodeAccountInfo(Json.String(oa, "emailAddress"), Json.String(oa, "accountUuid"),
                                             Json.String(oa, "organizationUuid"), Json.String(oa, "organizationName"));
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>The installed CLI version for the User-Agent: the newest folder in <c>~/.local/share/claude/versions</c>.</summary>
public static class ClaudeCliVersion
{
    public const string Fallback = "2.1.310";

    static string? cached;

    public static string Current()
    {
        if (cached is not null) return cached;
        string[] names;
        try { names = Directory.GetFileSystemEntries(Paths.Expand("~/.local/share/claude/versions")).Select(Path.GetFileName).OfType<string>().ToArray(); }
        catch (IOException) { names = Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { names = Array.Empty<string>(); }
        return cached = Pick(names);
    }

    public static string Pick(IEnumerable<string> names) =>
        names.Where(n => n.Contains('.') && n.All(c => char.IsAsciiDigit(c) || c == '.') && Version.TryParse(n, out _))
             .OrderByDescending(n => Version.Parse(n)).FirstOrDefault() ?? Fallback;

    public static string UserAgent => $"claude-cli/{Current()} (external, cli)";
}

public static class Paths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Expands a leading <c>~</c> and environment variables.</summary>
    public static string Expand(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (path == "~") return Home;
        if (path.StartsWith("~/") || path.StartsWith("~\\")) return Path.GetFullPath(Path.Combine(Home, path[2..]));
        return Path.GetFullPath(path);
    }

    /// <summary>Claude Code's config folder: <c>%CLAUDE_CONFIG_DIR%</c> when set, otherwise <c>~/.claude</c>.</summary>
    public static string DefaultClaudeConfigDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : "~/.claude";

    public static string AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageBar");
    public static string LocalAppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageBar");
    public static string ClaudeDesktop => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");

    /// <summary>Shows a path with the home folder as <c>~</c>, for display only.</summary>
    public static string Abbreviate(string path) =>
        path.StartsWith(Home, StringComparison.OrdinalIgnoreCase) ? "~" + path[Home.Length..] : path;

    internal static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
