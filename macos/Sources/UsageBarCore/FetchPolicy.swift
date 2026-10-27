import Foundation

public enum FetchTrigger: Sendable { case launch, timer, menuOpen, manual }

/// When an account may call its usage endpoint. Every call costs rate-limit budget, and the OAuth usage
/// endpoint (Claude Code, and the Claude app's token route) allows only about 5 requests every few
/// minutes, shared with Claude Code's own /usage. claude.ai's cookie endpoint has its own allowance. So:
/// - nothing fetches inside a Retry-After window;
/// - Refresh Now always may;
/// - the timer fetches once the account's background interval has passed, less 30 s of slack for the
///   60 s tick (the setting; at least 5 minutes for a sign-in on the OAuth endpoint);
/// - a launch fetches only when the last attempt is a full background interval old;
/// - opening the menu fetches when the last attempt is 2 minutes old, or the interval if that is shorter.
public enum FetchPolicy {
    /// How often the poll timer looks for accounts that are due.
    public static let tick: TimeInterval = 60
    /// The timer counts an account due this early, so tick jitter doesn't push the 2-minute setting to 3.
    public static let timerSlack: TimeInterval = 30
    /// Opening the menu refreshes numbers this old, even when the background interval is longer.
    public static let menuOpenAge: TimeInterval = 120
    /// The shortest background interval for a sign-in on the OAuth usage endpoint.
    public static let tokenMinimumInterval: TimeInterval = 300
    /// The wait after a 429 with no usable Retry-After. Anthropic often sends `Retry-After: 0`.
    public static let defaultRateLimitWait: TimeInterval = 300
    /// The longest Retry-After honoured.
    public static let maxRateLimitWait: TimeInterval = 3600

    /// How long to wait after a 429: Retry-After up to an hour, or 5 minutes when it is missing or 0.
    public static func rateLimitWait(_ retryAfter: TimeInterval?) -> TimeInterval {
        guard let ra = retryAfter, ra > 0 else { return defaultRateLimitWait }
        return min(ra, maxRateLimitWait)
    }

    /// Whether the account calls the OAuth usage endpoint: Claude Code always, the Claude app when its
    /// last fetch used the token route.
    public static func isTokenSource(_ kind: AccountSource.Kind, lastRoute: String?) -> Bool {
        switch kind {
        case .claudeCode: return true
        case .claudeDesktop: return lastRoute == "token"
        case .safariSession: return false
        }
    }

    /// The account's background interval: the refresh setting (2 minutes if it isn't one of the options),
    /// and never under 5 minutes for a token source.
    public static func backgroundInterval(userMinutes: Int, tokenSource: Bool) -> TimeInterval {
        let minutes = AppSettings.refreshOptions.contains(userMinutes) ? userMinutes : 2
        let interval = TimeInterval(minutes) * 60
        return tokenSource ? max(interval, tokenMinimumInterval) : interval
    }

    public static func shouldFetch(_ trigger: FetchTrigger, lastAttempt: Date?, waitUntil: Date?,
                                   backgroundInterval: TimeInterval, now: Date) -> Bool {
        if let wait = waitUntil, now < wait { return false }
        guard trigger != .manual, let last = lastAttempt else { return true }
        let elapsed = now.timeIntervalSince(last)
        switch trigger {
        case .manual: return true
        case .timer: return elapsed >= backgroundInterval - timerSlack
        case .launch: return elapsed >= backgroundInterval
        case .menuOpen: return elapsed >= min(backgroundInterval, menuOpenAge)
        }
    }
}

public struct AccountPresentation: Equatable {
    public var headerNote: String?
    public var inlineIssue: AccountIssue?
    public var greysValues: Bool
    /// A 429 with numbers already on screen is a pause, not an error: keep the numbers as they are and
    /// say so in the account header. Without numbers, or for any other issue, show the issue inline.
    public init(issue: AccountIssue?, hasData: Bool, timeZone: TimeZone = .current) {
        if case .rateLimited(let until)? = issue, hasData {
            headerNote = "Paused by rate limit · retrying \(Formatters.clockTime(until, timeZone: timeZone))"
            inlineIssue = nil
            greysValues = false
        } else {
            headerNote = nil
            inlineIssue = issue
            greysValues = issue?.greysValues ?? false
        }
    }
}

public struct PersistedAccountState: Codable, Equatable, Sendable {
    public var snapshot: UsageSnapshot?
    public var lastSuccess: Date?
    public var lastAttempt: Date?
    public var waitUntil: Date?
    public var email: String?
    public init(snapshot: UsageSnapshot?, lastSuccess: Date?, lastAttempt: Date?, waitUntil: Date?, email: String?) {
        self.snapshot = snapshot; self.lastSuccess = lastSuccess; self.lastAttempt = lastAttempt
        self.waitUntil = waitUntil; self.email = email
    }
}
