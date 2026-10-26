namespace UsageBar.Core;

/// <summary>
/// What a first launch found signed in on this PC (decision 16). Gathered without reading a secret or calling DPAPI:
/// whether Claude Code's credentials file exists, <c>~/.claude.json</c>, and the Claude app's config.json.
/// </summary>
public sealed record FoundSignIns(bool ClaudeCodeLogin = false, string? ClaudeCodeAccountUuid = null,
                                  bool ClaudeAppHasTokenCache = false, string? ClaudeAppAccountUuid = null)
{
    /// <summary>
    /// Sources to create accounts for, in order. The Claude app is skipped when it is signed in to the Claude Code
    /// account: the fetcher refuses that pairing, so the account could never show numbers.
    /// </summary>
    public IReadOnlyList<SourceKind> Kinds
    {
        get
        {
            var kinds = new List<SourceKind>();
            if (ClaudeCodeLogin) kinds.Add(SourceKind.ClaudeCode);
            if (ClaudeAppHasTokenCache && ClaudeAppAccountUuid is { Length: > 0 } app
                && !string.Equals(app, ClaudeCodeAccountUuid, StringComparison.OrdinalIgnoreCase))
                kinds.Add(SourceKind.ClaudeDesktop);
            return kinds;
        }
    }
}

/// <summary>First launch only: finds which sign-ins exist, so the fresh settings hold just those accounts.</summary>
public static class SignInDetector
{
    public static FoundSignIns Detect() =>
        Detect(Paths.Expand(Paths.DefaultClaudeConfigDir), Paths.Expand("~/.claude.json"), Paths.ClaudeDesktop);

    public static FoundSignIns Detect(string claudeConfigDir, string claudeJsonPath, string claudeAppDir)
    {
        // The credentials file's presence is the sign-in; its token is never read here.
        var login = File.Exists(Path.Combine(claudeConfigDir, ".credentials.json"));
        var cli = ClaudeCodeAccountInfo.Read(claudeJsonPath);
        DesktopConfig? app = null;
        try { app = DesktopConfig.Parse(File.ReadAllBytes(Path.Combine(claudeAppDir, "config.json"))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CredentialException) { }
        return new FoundSignIns(login, cli?.AccountUuid,
                                app?.EncryptedCaches.Any(c => c.Key == "oauth:tokenCacheV2") ?? false, app?.LastKnownAccountUuid);
    }
}

public enum DropdownView { Empty, Stacked, OneAtATime }

/// <summary>What the flyout shows for the chosen layout and number of accounts.</summary>
public readonly record struct DropdownContent(DropdownView View, bool Tabs)
{
    /// <summary>No accounts: the empty state whatever the layout. One at a time: tabs only when there is another account to switch to.</summary>
    public static DropdownContent For(DropdownLayout layout, int accountCount) =>
        accountCount == 0 ? new(DropdownView.Empty, false)
        : layout == DropdownLayout.BothAccounts ? new(DropdownView.Stacked, false)
        : new(DropdownView.OneAtATime, accountCount > 1);
}
