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
/// When an account may call its usage endpoint (decision 18). Every call costs rate-limit budget, and the OAuth usage
/// endpoint (Claude Code, and the Claude app's token route) allows only about 5 requests every few minutes, shared with
/// Claude Code's own /usage. claude.ai's cookie endpoint has its own allowance. So:
/// <list type="bullet">
/// <item>nothing fetches inside a Retry-After window;</item>
/// <item>Refresh Now always may;</item>
/// <item>the timer fetches once the account's background interval has passed, less 30 s of slack for the 60 s tick
/// (the setting; at least 5 minutes for a sign-in on the OAuth endpoint);</item>
/// <item>a launch fetches only when the last attempt is a full background interval old;</item>
/// <item>opening the flyout fetches when the last attempt is 2 minutes old, or the interval if that is shorter.</item>
/// </list>
/// </summary>
public static class FetchPolicy
{
    /// <summary>How often the poll timer looks for accounts that are due.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    /// <summary>The timer counts an account due this early, so tick jitter doesn't push the 2-minute setting to 3.</summary>
    public const double TimerSlack = 30;
    /// <summary>Opening the flyout refreshes numbers this old, even when the background interval is longer.</summary>
    public const double MenuOpenAge = 120;
    /// <summary>The shortest background interval for a sign-in on the OAuth usage endpoint.</summary>
    public const double TokenMinimumInterval = 300;
    /// <summary>The wait after a 429 with no usable Retry-After. Anthropic often sends <c>Retry-After: 0</c>.</summary>
    public const double DefaultRateLimitWait = 300;
    /// <summary>The longest Retry-After honoured.</summary>
    public const double MaxRateLimitWait = 3600;

    /// <summary>How long to wait after a 429: Retry-After up to an hour, or 5 minutes when it is missing or 0.</summary>
    public static double RateLimitWait(double? retryAfter) =>
        retryAfter is { } ra && ra > 0 ? Math.Min(ra, MaxRateLimitWait) : DefaultRateLimitWait;

    /// <summary>Whether the account calls the OAuth usage endpoint: Claude Code always, the Claude app when its last fetch used the token route.</summary>
    public static bool IsTokenSource(SourceKind kind, string? lastRoute) => kind switch
    {
        SourceKind.ClaudeCode => true,
        SourceKind.ClaudeDesktop => lastRoute == "token",
        _ => false,
    };

    /// <summary>The account's background interval in seconds: the refresh setting (2 minutes if it isn't one of the options), and never under 5 minutes for a token source.</summary>
    public static double BackgroundInterval(int userMinutes, bool tokenSource)
    {
        var interval = (AppSettings.RefreshOptions.Contains(userMinutes) ? userMinutes : 2) * 60.0;
        return tokenSource ? Math.Max(interval, TokenMinimumInterval) : interval;
    }

    public static bool ShouldFetch(FetchTrigger trigger, DateTimeOffset? lastAttempt, DateTimeOffset? waitUntil,
                                   double backgroundInterval, DateTimeOffset now)
    {
        if (waitUntil is { } wait && now < wait) return false;
        if (trigger == FetchTrigger.Manual || lastAttempt is not { } last) return true;
        var elapsed = (now - last).TotalSeconds;
        return trigger switch
        {
            FetchTrigger.Timer => elapsed >= backgroundInterval - TimerSlack,
            FetchTrigger.Launch => elapsed >= backgroundInterval,
            _ => elapsed >= Math.Min(backgroundInterval, MenuOpenAge),
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
