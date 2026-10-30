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
        byte[] data;
        try { data = Paths.ReadShared(Path.Combine(Paths.Expand(configDir), FileName)); }
        catch (FileNotFoundException) { throw new CredentialException(CredentialError.NotFound); }
        catch (DirectoryNotFoundException) { throw new CredentialException(CredentialError.NotFound); }
        // Access denied, a sharing violation while Claude Code rewrites it, a path too long: can't read it now.
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            throw new CredentialException(CredentialError.Denied);
        }
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
            DateTimeOffset? expires = null;
            if (Json.Number(oauth, "expiresAt") is { } ms)
            {
                if (!(ms >= MinMs && ms <= MaxMs)) throw new CredentialException(CredentialError.Malformed);
                expires = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
            }
            return new CliToken(token, expires, Json.String(oauth, "subscriptionType"));
        }
        catch (JsonException) { throw new CredentialException(CredentialError.Malformed); }
    }

    static readonly double MinMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds(), MaxMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    internal static byte[] StripBom(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b[3..] : b;
}

/// <summary>The CLI's account details from <c>~/.claude.json</c>: the Claude Code source's email, and its account uuid for first-run detection.</summary>
public sealed record ClaudeCodeAccountInfo(string? Email, string? AccountUuid, string? OrganizationUuid, string? OrganizationName)
{
    /// <summary>The account file for <paramref name="path"/>, or for Claude Code's current config folder.</summary>
    public static ClaudeCodeAccountInfo? Read(string? path = null)
    {
        try { return Parse(Paths.ReadShared(path ?? Paths.ClaudeAccountInfo(Paths.DefaultClaudeConfigDir))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
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

    /// <summary>Read on every request, as on the Mac, so an update to Claude Code shows without restarting.</summary>
    public static string Current()
    {
        string[] names;
        try { names = Directory.GetFileSystemEntries(Paths.Expand("~/.local/share/claude/versions")).Select(Path.GetFileName).OfType<string>().ToArray(); }
        catch (IOException) { names = Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { names = Array.Empty<string>(); }
        return Pick(names);
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
    /// <summary>
    /// Opens another app's file for reading without blocking it: Claude Code rewrites its files in place and the
    /// Claude app renames over config.json, so the read shares write and delete access.
    /// </summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static byte[] ReadShared(string path)
    {
        using var f = OpenShared(path);
        using var m = new MemoryStream();
        f.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>
    /// Claude Code's account file (<c>.claude.json</c>) for a config folder: beside the default <c>~/.claude</c>, but
    /// inside a folder named by <c>CLAUDE_CONFIG_DIR</c> (checked on Windows 11, Claude Code 2.1).
    /// </summary>
    public static string ClaudeAccountInfo(string configDir) =>
        ClaudeAccountInfo(configDir, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : null);

    public static string ClaudeAccountInfo(string configDir, string? envConfigDir)
    {
        static string Norm(string p) => Path.TrimEndingDirectorySeparator(Expand(p));
        var dir = Norm(configDir);
        var isDefault = string.Equals(dir, Norm("~/.claude"), StringComparison.OrdinalIgnoreCase)
                        && !(envConfigDir is not null && string.Equals(Norm(envConfigDir), dir, StringComparison.OrdinalIgnoreCase));
        return isDefault ? Expand("~/.claude.json") : Path.Combine(dir, ".claude.json");
    }

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
