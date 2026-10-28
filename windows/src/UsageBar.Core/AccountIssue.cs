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
/// When an account may call its usage endpoint (decision 19). Every call costs rate-limit budget. Anthropic publishes no
/// limit for the OAuth usage endpoint (Claude Code, and the Claude app's token route); it is shared with Claude Code's own
/// /usage and every other poller on the account, and its 429s carry Retry-After values from 0 to 3600 s. claude.ai's
/// cookie endpoint has its own allowance. So:
/// <list type="bullet">
/// <item>nothing fetches inside a Retry-After window;</item>
/// <item>a token source polls no faster than every 15 minutes, and never on a flyout open; Refresh Now fetches it only
/// once the last attempt is 5 minutes old;</item>
/// <item>a cookie source polls at the setting, and the flyout or Refresh Now fetches it as before;</item>
/// <item>each 429 doubles the account's background interval (up to an hour); 3 good reads in a row step it back one level;</item>
/// <item>the timer fetches once the background interval has passed, less 30 s of slack for the 60 s tick;</item>
/// <item>a launch fetches only when the last attempt is a full background interval old.</item>
/// </list>
/// </summary>
public static class FetchPolicy
{
    /// <summary>How often the poll timer looks for accounts that are due.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    /// <summary>The timer counts an account due this early, so tick jitter doesn't push the 2-minute setting to 3.</summary>
    public const double TimerSlack = 30;
    /// <summary>Opening the flyout refreshes a cookie source's numbers this old, even when its interval is longer.</summary>
    public const double MenuOpenAge = 120;
    /// <summary>The shortest background interval for a sign-in on the OAuth usage endpoint.</summary>
    public const double TokenMinimumInterval = 900;
    /// <summary>Refresh Now on a token source waits this long after the last attempt.</summary>
    public const double ManualTokenAge = 300;
    /// <summary>The wait after a 429 with no usable Retry-After. Anthropic often sends <c>Retry-After: 0</c>.</summary>
    public const double DefaultRateLimitWait = 300;
    /// <summary>The longest Retry-After honoured (the jitter comes on top).</summary>
    public const double MaxRateLimitWait = 3600;
    /// <summary>Added to every 429 wait, so the retry never lands on the block's boundary: 2 to 10 minutes.</summary>
    public const double RateLimitJitterMin = 120, RateLimitJitterMax = 600;
    /// <summary>The longest background interval backoff reaches.</summary>
    public const double MaxBackoffInterval = 3600;

    /// <summary>Retry-After up to an hour, or 5 minutes when it is missing, 0 or negative. The jitter is separate.</summary>
    public static double RateLimitWait(double? retryAfter) =>
        retryAfter is { } ra && ra > 0 ? Math.Min(ra, MaxRateLimitWait) : DefaultRateLimitWait;

    /// <summary>A random 2 to 10 minutes to add to a 429 wait, from 64 random bits. One draw, so any generator works.</summary>
    public static double RateLimitJitter(Func<ulong> next) => JitterFromUnit((next() >> 11) * (1.0 / (1UL << 53)));

    public static double RateLimitJitter() => JitterFromUnit(Random.Shared.NextDouble());

    /// <summary><paramref name="unit"/> is in [0, 1).</summary>
    static double JitterFromUnit(double unit) => RateLimitJitterMin + unit * (RateLimitJitterMax - RateLimitJitterMin);

    /// <summary>Whether the account calls the OAuth usage endpoint: Claude Code always, the Claude app when its last fetch used the token route.</summary>
    public static bool IsTokenSource(SourceKind kind, string? lastRoute) => kind switch
    {
        SourceKind.ClaudeCode => true,
        SourceKind.ClaudeDesktop => lastRoute == "token",
        _ => false,
    };

    /// <summary>
    /// The account's background interval in seconds: the refresh setting (2 minutes if it isn't one of the options), never
    /// under 15 minutes for a token source, doubled for each backoff level up to an hour.
    /// </summary>
    public static double BackgroundInterval(int userMinutes, bool tokenSource, int backoffLevel = 0)
    {
        var interval = (AppSettings.RefreshOptions.Contains(userMinutes) ? userMinutes : 2) * 60.0;
        return BackedOff(tokenSource ? Math.Max(interval, TokenMinimumInterval) : interval, backoffLevel);
    }

    static double BackedOff(double baseInterval, int level)
    {
        level = Math.Clamp(level, 0, MaxBackoffLevel(baseInterval));
        return Math.Min(baseInterval * Math.Pow(2, level), Math.Max(baseInterval, MaxBackoffInterval));
    }

    /// <summary>The level at which the interval reaches an hour: 2 from 15 minutes, 5 from 2 minutes.</summary>
    public static int MaxBackoffLevel(double baseInterval)
    {
        if (baseInterval <= 0) return 0;
        var level = 0;
        while (baseInterval * Math.Pow(2, level) < MaxBackoffInterval) level++;
        return level;
    }

    /// <summary>
    /// When the timer will next fetch the account on its own: after any wait, once the interval has passed since the
    /// last attempt (the 60 s tick can add up to a minute). Never before now.
    /// </summary>
    public static DateTimeOffset NextCheck(DateTimeOffset? lastAttempt, DateTimeOffset? waitUntil, double backgroundInterval, DateTimeOffset now)
    {
        var next = now;
        if (lastAttempt is { } last && last.AddSeconds(backgroundInterval) > next) next = last.AddSeconds(backgroundInterval);
        if (waitUntil is { } wait && wait > next) next = wait;
        return next;
    }

    public static bool ShouldFetch(FetchTrigger trigger, DateTimeOffset? lastAttempt, DateTimeOffset? waitUntil,
                                   double backgroundInterval, DateTimeOffset now, bool tokenSource = false)
    {
        if (waitUntil is { } wait && now < wait) return false;
        if (trigger == FetchTrigger.MenuOpen && tokenSource) return false;
        if (lastAttempt is not { } last) return true;
        var elapsed = (now - last).TotalSeconds;
        return trigger switch
        {
            FetchTrigger.Manual => !tokenSource || elapsed >= ManualTokenAge,
            FetchTrigger.Timer => elapsed >= backgroundInterval - TimerSlack,
            FetchTrigger.Launch => elapsed >= backgroundInterval,
            _ => elapsed >= Math.Min(backgroundInterval, MenuOpenAge),
        };
    }
}

/// <summary>
/// One account's 429 backoff: each 429 raises the level (doubling its background interval, up to an hour); 3 good reads
/// in a row lower it by one. Any failed read restarts the count.
/// </summary>
public sealed record FetchBackoff
{
    public const int SuccessesToStepDown = 3;
    public int Level { get; set; }
    /// <summary>Good reads in a row since the last level change or failure.</summary>
    public int Successes { get; set; }

    public FetchBackoff() { }

    public FetchBackoff(int level, int successes) { Level = level; Successes = successes; }

    public void RateLimited(double baseInterval)
    {
        Level = Math.Min(Level + 1, FetchPolicy.MaxBackoffLevel(baseInterval));
        Successes = 0;
    }

    public void Succeeded()
    {
        if (Level == 0) { Successes = 0; return; }
        if (++Successes >= SuccessesToStepDown)
        {
            Level--;
            Successes = 0;
        }
    }

    public void Failed() => Successes = 0;
}

/// <summary>
/// A 429 with numbers already on screen is a pause, not an error: keep the numbers as they are and say so in the
/// account header. Without numbers, or for any other issue, show the issue inline. <c>nextCheck</c> is set when Refresh
/// Now (or Connect) was skipped: the header says when the next check is, unless a rate-limit note already does.
/// No-break spaces keep the time with its phrase.
/// </summary>
public sealed class AccountPresentation
{
    const string Nbsp = " ";

    public string? HeaderNote { get; }
    public AccountIssue? InlineIssue { get; }
    public bool GreysValues { get; }

    public AccountPresentation(AccountIssue? issue, bool hasData, DateTimeOffset? nextCheck = null, TimeZoneInfo? timeZone = null)
    {
        if (issue is { Kind: IssueKind.RateLimited } && hasData)
        {
            HeaderNote = "Rate limited · next check" + Nbsp + Time(issue.Until ?? DateTimeOffset.UtcNow, timeZone);
        }
        else
        {
            InlineIssue = issue;
            GreysValues = issue?.GreysValues ?? false;
            if (nextCheck is { } next && issue?.Kind != IssueKind.RateLimited) HeaderNote = "Next check" + Nbsp + Time(next, timeZone);
        }
    }

    static string Time(DateTimeOffset date, TimeZoneInfo? timeZone) => Formatters.ClockTime(date, timeZone).Replace(" ", Nbsp);
}

/// <summary>
/// Saved to disk so a relaunch neither fetches inside a wait nor forgets the backoff. Fields added after 0.1.6 are
/// optional: a file written by an earlier version still loads.
/// </summary>
public sealed record PersistedAccountState(UsageSnapshot? Snapshot, DateTimeOffset? LastSuccess, DateTimeOffset? LastAttempt,
                                           DateTimeOffset? WaitUntil, string? Email, FetchBackoff? Backoff = null,
                                           string? LastRoute = null)
{
    /// <summary>
    /// A wait saved by 0.1.6 or earlier (no backoff saved) ends exactly on the Retry-After boundary. Count it as one 429:
    /// add the jitter and raise the backoff level. Returns whether it changed anything.
    /// </summary>
    public bool UpgradeEarlierWait(double baseInterval, double jitter, DateTimeOffset now, out PersistedAccountState result)
    {
        result = this;
        if (Backoff is not null || WaitUntil is not { } wait || wait <= now) return false;
        var backoff = new FetchBackoff();
        backoff.RateLimited(baseInterval);
        result = this with { WaitUntil = wait.AddSeconds(jitter), Backoff = backoff };
        return true;
    }
}
