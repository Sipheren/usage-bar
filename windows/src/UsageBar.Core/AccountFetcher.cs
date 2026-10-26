namespace UsageBar.Core;

public sealed class FetchReport
{
    public UsageSnapshot? Snapshot { get; set; }
    public AccountIssue? Issue { get; set; }
    /// <summary>Last HTTP status from the usage endpoint, if a request was made.</summary>
    public int? HttpStatus { get; set; }
    public double? RetryAfter { get; set; }
    /// <summary>What Settings shows as the account's identity: an email or an organization name.</summary>
    public string? Email { get; set; }
    /// <summary>Claude app: key path of the chosen token-cache entry (no secrets), kept per account uuid.</summary>
    public (string AccountUuid, string Path)? DesktopChoice { get; set; }
    /// <summary>"token", "cookie" or "fixture", for the log.</summary>
    public string? Route { get; set; }
}

public sealed record DesktopContext(bool Connected, IReadOnlyDictionary<string, string> CachedChoice, string? KnownEmail);

/// <summary>The Claude app source, implemented by the Windows app (it needs DPAPI). Null when unavailable.</summary>
public interface IDesktopFetcher
{
    Task<FetchReport> FetchAsync(DesktopContext ctx, DateTimeOffset now, CancellationToken ct);
}

/// <summary>One fetch per account. Never refreshes or writes any app's credentials.</summary>
public sealed class AccountFetcher(HttpClient http, IDesktopFetcher? desktop = null, Func<string, HttpClient>? fixtureHttp = null)
{
    public static readonly AccountIssue ClaudeCodeRejected = AccountIssue.Rejected("Claude Code login rejected — run `claude` once");
    public static readonly AccountIssue ClaudeCodeMissing = AccountIssue.NotSignedIn("Not signed in to Claude Code — run `claude` once");

    readonly UsageClient client = new(http);

    public async Task<FetchReport> FetchAsync(Account account, DesktopContext ctx, DateTimeOffset now, CancellationToken ct = default)
    {
        var report = account.Source.Kind switch
        {
            SourceKind.ClaudeCode => await FetchClaudeCodeAsync(account.Source.Dir ?? Paths.DefaultClaudeConfigDir, now, ct),
            SourceKind.Fixture => await FetchFixtureAsync(account.Source.Dir ?? "", ct),
            _ => desktop is null ? new FetchReport { Issue = AccountIssue.Unsupported("The Claude app source isn't available") }
                                 : await desktop.FetchAsync(ctx, now, ct),
        };
        var kind = account.Source.Kind.ToString();
        if (report.Snapshot is not null)
            Log.Info($"fetch {kind}: ok, HTTP {report.HttpStatus ?? 0}, {report.Snapshot.Limits.Count} limits, route {report.Route ?? "-"}");
        else
            Log.Info($"fetch {kind}: {report.Issue?.LogName ?? "noData"}, HTTP {report.HttpStatus ?? 0}");
        return report;
    }

    /// <summary>The account file that goes with a Claude Code config folder.</summary>
    public static string AccountInfoPath(string configDir)
    {
        var dir = Paths.Expand(configDir);
        return string.Equals(dir, Paths.Expand("~/.claude"), StringComparison.OrdinalIgnoreCase)
            ? Paths.Expand("~/.claude.json") : Path.Combine(dir, ".claude.json");
    }

    public async Task<FetchReport> FetchClaudeCodeAsync(string configDir, DateTimeOffset now, CancellationToken ct)
    {
        var report = new FetchReport { Route = "token", Email = ClaudeCodeAccountInfo.Read(AccountInfoPath(configDir))?.Email };
        CliToken token;
        try { token = ClaudeCodeCredentials.Read(configDir); }
        catch (CredentialException e)
        {
            report.Issue = e.Error == CredentialError.NotFound ? ClaudeCodeMissing
                : AccountIssue.Rejected("Couldn't read the Claude Code login");
            return report;
        }
        if (token.IsExpired(now))
        {
            Log.Info("claudeCode: login expired");
            report.Issue = AccountIssue.ExpiredCli;
            return report;
        }
        await CallUsageAsync(token.AccessToken, report, ClaudeCodeRejected, ct);
        return report;
    }

    /// <summary>Calls the OAuth usage endpoint and records the outcome on <paramref name="report"/>.</summary>
    public async Task CallUsageAsync(string token, FetchReport report, AccountIssue rejected, CancellationToken ct)
    {
        try
        {
            var o = await client.FetchUsageAsync(token, ct);
            report.HttpStatus = o.Status;
            switch (o.Kind)
            {
                case FetchOutcomeKind.Ok: report.Snapshot = o.Snapshot; report.Issue = null; break;
                case FetchOutcomeKind.Unauthorized: report.Issue = rejected; break;
                case FetchOutcomeKind.ScopeError: report.Issue = AccountIssue.Scope; break;
                case FetchOutcomeKind.RateLimited:
                    report.RetryAfter = o.RetryAfter;
                    report.Issue = AccountIssue.RateLimited(DateTimeOffset.UtcNow.AddSeconds(o.RetryAfter ?? 300));
                    break;
                case FetchOutcomeKind.Http: report.Issue = AccountIssue.Http(o.Status); break;
                default: report.Issue = AccountIssue.Rejected("Unexpected usage response"); break;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            report.Issue = AccountIssue.Network(NetworkMessage.Short(e));
        }
    }

    public Task<(Profile? Profile, int Status)> FetchProfileAsync(string token, CancellationToken ct) => client.FetchProfileAsync(token, ct);

    /// <summary>
    /// Runs the claude.ai cookie flow and records the outcome. <paramref name="where"/> names what to open on a
    /// Cloudflare check; <paramref name="who"/> names the source when it's signed in to the personal account.
    /// </summary>
    public static async Task ApplyCookieFlowAsync(ClaudeAiCookieClient cookies, IReadOnlyDictionary<string, string> jar, string? personalOrg,
                                                  FetchReport report, AccountIssue rejected, string where, string who, CancellationToken ct)
    {
        var o = await cookies.FetchUsageAsync(jar, personalOrg, ct);
        report.HttpStatus = o.Status;
        report.Issue = o.Kind switch
        {
            CookieOutcomeKind.Ok => null,
            CookieOutcomeKind.NoSession => AccountIssue.NotSignedIn("No claude.ai sign-in found"),
            CookieOutcomeKind.Cloudflare => AccountIssue.Cloudflare(where),
            CookieOutcomeKind.Unauthorized => rejected,
            CookieOutcomeKind.ScopeError => AccountIssue.Scope,
            CookieOutcomeKind.SignedInToPersonal => AccountIssue.SignedInToPersonal(who),
            CookieOutcomeKind.NoOrganizations => AccountIssue.Rejected("No organizations on this claude.ai session"),
            CookieOutcomeKind.Http => AccountIssue.Http(o.Status),
            CookieOutcomeKind.Undecodable => AccountIssue.Rejected("Unexpected usage response"),
            _ => AccountIssue.Network(o.Detail ?? "request failed"),
        };
        if (o.Kind == CookieOutcomeKind.Ok)
        {
            report.Snapshot = o.Snapshot;
            report.Email ??= o.Org?.Name;
        }
    }

    /// <summary>Test data: the claude.ai flow answered from a fixtures folder, never the network.</summary>
    async Task<FetchReport> FetchFixtureAsync(string dir, CancellationToken ct)
    {
        var report = new FetchReport { Route = "fixture" };
        if (!Directory.Exists(dir))
        {
            report.Issue = AccountIssue.NotSignedIn("Fixtures folder not found");
            return report;
        }
        var cookies = new ClaudeAiCookieClient(fixtureHttp?.Invoke(dir) ?? new HttpClient(new FixtureHandler(dir)));
        var personalOrg = ClaudeCodeAccountInfo.Read()?.OrganizationUuid;
        var jar = new Dictionary<string, string> { ["sessionKey"] = "fixture" };
        await ApplyCookieFlowAsync(cookies, jar, personalOrg, report, AccountIssue.NotSignedIn("Fixture sign-in rejected"),
                                   "your browser", "This session", ct);
        return report;
    }
}
