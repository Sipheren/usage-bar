namespace UsageBar.Core;

public enum IssueKind
{
    ExpiredCli, ExpiredDesktop, NotConnected, Cloudflare, Scope, SignedInToPersonal,
    NotSignedIn, Network, Rejected, Http, RateLimited, Unsupported,
}

/// <summary>Why an account shows no fresh numbers. <c>Text</c> carries the wording for the kinds that need it.</summary>
public sealed record AccountIssue(IssueKind Kind, string? Text = null, int Code = 0, DateTimeOffset? Until = null)
{
    public static readonly AccountIssue ExpiredCli = new(IssueKind.ExpiredCli);
    public static readonly AccountIssue ExpiredDesktop = new(IssueKind.ExpiredDesktop);
    /// <summary>The Claude app source hasn't been allowed yet (Connect…), so nothing has been read.</summary>
    public static readonly AccountIssue NotConnected = new(IssueKind.NotConnected);
    public static readonly AccountIssue Scope = new(IssueKind.Scope);
    /// <summary>claude.ai returned a Cloudflare challenge; <paramref name="where"/> names what to open.</summary>
    public static AccountIssue Cloudflare(string where) => new(IssueKind.Cloudflare, where);
    public static AccountIssue SignedInToPersonal(string who) => new(IssueKind.SignedInToPersonal, who);
    public static AccountIssue NotSignedIn(string message) => new(IssueKind.NotSignedIn, message);
    public static AccountIssue Network(string detail) => new(IssueKind.Network, detail);
    public static AccountIssue Rejected(string message) => new(IssueKind.Rejected, message);
    public static AccountIssue Http(int code) => new(IssueKind.Http, Code: code);
    public static AccountIssue RateLimited(DateTimeOffset until) => new(IssueKind.RateLimited, Until: until);
    public static AccountIssue Unsupported(string message) => new(IssueKind.Unsupported, message);

    public string Message(TimeZoneInfo? timeZone = null) => Kind switch
    {
        IssueKind.ExpiredCli => "Claude Code login expired — run `claude` once",
        IssueKind.ExpiredDesktop => "Open the Claude app to refresh its sign-in",
        IssueKind.NotConnected => "Allow Usage Bar to read the Claude app's sign-in",
        IssueKind.Cloudflare => "Blocked by a Cloudflare check — open claude.ai in " + Text,
        IssueKind.Scope => "This sign-in can't read usage (scope)",
        IssueKind.SignedInToPersonal => Text + " is signed in to your Personal account",
        IssueKind.Network => "Network error: " + Text,
        IssueKind.Http => $"Usage request failed (HTTP {Code})",
        IssueKind.RateLimited => "Rate limited — retrying after " + Formatters.ClockTime(Until ?? DateTimeOffset.UtcNow, timeZone),
        _ => Text ?? "",
    };

    /// <summary>Issues that mean the shown values are old.</summary>
    public bool GreysValues => Kind != IssueKind.NotConnected;

    /// <summary>Case name only (no associated text), safe to log.</summary>
    public string LogName => Kind == IssueKind.Http ? $"http{Code}" : char.ToLowerInvariant(Kind.ToString()[0]) + Kind.ToString()[1..];
}

public enum FetchTrigger { Launch, Timer, MenuOpen, Manual }

/// <summary>
/// When an account may call its usage endpoint. Every call costs rate-limit budget, so: nothing fetches inside a
/// Retry-After window; a launch or a flyout open fetches only when the last attempt is at least one refresh interval
/// old; the timer and Refresh Now always may.
/// </summary>
public static class FetchPolicy
{
    public static bool ShouldFetch(FetchTrigger trigger, DateTimeOffset? lastAttempt, DateTimeOffset? waitUntil,
                                   double intervalSeconds, DateTimeOffset now)
    {
        if (waitUntil is { } wait && now < wait) return false;
        return trigger switch
        {
            FetchTrigger.Timer or FetchTrigger.Manual => true,
            _ => lastAttempt is not { } last || (now - last).TotalSeconds >= intervalSeconds,
        };
    }
}

/// <summary>
/// A 429 with numbers already on screen is a pause, not an error: keep the numbers as they are and say so in the
/// account header. Without numbers, or for any other issue, show the issue inline.
/// </summary>
public sealed class AccountPresentation
{
    public string? HeaderNote { get; }
    public AccountIssue? InlineIssue { get; }
    public bool GreysValues { get; }

    public AccountPresentation(AccountIssue? issue, bool hasData, TimeZoneInfo? timeZone = null)
    {
        if (issue is { Kind: IssueKind.RateLimited } && hasData)
        {
            HeaderNote = "Paused by rate limit · retrying " + Formatters.ClockTime(issue.Until ?? DateTimeOffset.UtcNow, timeZone);
        }
        else
        {
            InlineIssue = issue;
            GreysValues = issue?.GreysValues ?? false;
        }
    }
}

public sealed record PersistedAccountState(UsageSnapshot? Snapshot, DateTimeOffset? LastSuccess, DateTimeOffset? LastAttempt,
                                           DateTimeOffset? WaitUntil, string? Email);
