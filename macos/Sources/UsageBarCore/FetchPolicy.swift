import Foundation

public enum FetchTrigger: Sendable { case launch, timer, menuOpen, manual }

/// When an account may call its usage endpoint (decision 19). Every call costs rate-limit budget.
/// Anthropic publishes no limit for the OAuth usage endpoint (Claude Code, and the Claude app's token
/// route); it is shared with Claude Code's own /usage and every other poller on the account, and its
/// 429s carry Retry-After values from 0 to 3600 s. claude.ai's cookie endpoint has its own allowance. So:
/// - nothing fetches inside a Retry-After window;
/// - a token source polls no faster than every 15 minutes, and never on a menu open; Refresh Now
///   fetches it only once the last attempt is 5 minutes old;
/// - a cookie source polls at the setting, and the menu or Refresh Now fetches it as before;
/// - each 429 doubles the account's background interval (up to an hour); 3 good reads in a row step it
///   back one level;
/// - the timer fetches once the background interval has passed, less 30 s of slack for the 60 s tick;
/// - a launch fetches only when the last attempt is a full background interval old.
public enum FetchPolicy {
    /// How often the poll timer looks for accounts that are due.
    public static let tick: TimeInterval = 60
    /// The timer counts an account due this early, so tick jitter doesn't push the 2-minute setting to 3.
    public static let timerSlack: TimeInterval = 30
    /// Opening the menu refreshes a cookie source's numbers this old, even when its interval is longer.
    public static let menuOpenAge: TimeInterval = 120
    /// The shortest background interval for a sign-in on the OAuth usage endpoint.
    public static let tokenMinimumInterval: TimeInterval = 900
    /// Refresh Now on a token source waits this long after the last attempt.
    public static let manualTokenAge: TimeInterval = 300
    /// The wait after a 429 with no usable Retry-After. Anthropic often sends `Retry-After: 0`.
    public static let defaultRateLimitWait: TimeInterval = 300
    /// The longest Retry-After honoured (the jitter comes on top).
    public static let maxRateLimitWait: TimeInterval = 3600
    /// Added to every 429 wait, so the retry never lands on the block's boundary.
    public static let rateLimitJitterRange: ClosedRange<TimeInterval> = 120...600
    /// The longest background interval backoff reaches.
    public static let maxBackoffInterval: TimeInterval = 3600

    /// Retry-After up to an hour, or 5 minutes when it is missing, 0 or negative. The jitter is separate.
    public static func rateLimitWait(_ retryAfter: TimeInterval?) -> TimeInterval {
        guard let ra = retryAfter, ra > 0 else { return defaultRateLimitWait }
        return min(ra, maxRateLimitWait)
    }

    /// A random 2 to 10 minutes to add to a 429 wait. One draw, no rejection loop, so any generator works.
    public static func rateLimitJitter<G: RandomNumberGenerator>(using rng: inout G) -> TimeInterval {
        let unit = Double(rng.next() >> 11) * 0x1p-53   // [0, 1)
        let r = rateLimitJitterRange
        return r.lowerBound + unit * (r.upperBound - r.lowerBound)
    }

    public static func rateLimitJitter() -> TimeInterval {
        var rng = SystemRandomNumberGenerator()
        return rateLimitJitter(using: &rng)
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
    /// never under 15 minutes for a token source, doubled for each backoff level up to an hour.
    public static func backgroundInterval(userMinutes: Int, tokenSource: Bool, backoffLevel: Int = 0) -> TimeInterval {
        let minutes = AppSettings.refreshOptions.contains(userMinutes) ? userMinutes : 2
        let base = tokenSource ? max(TimeInterval(minutes) * 60, tokenMinimumInterval) : TimeInterval(minutes) * 60
        return backedOff(base, level: backoffLevel)
    }

    static func backedOff(_ base: TimeInterval, level: Int) -> TimeInterval {
        let level = min(max(0, level), maxBackoffLevel(baseInterval: base))
        return min(base * pow(2, Double(level)), max(base, maxBackoffInterval))
    }

    /// The level at which the interval reaches an hour: 2 from 15 minutes, 5 from 2 minutes.
    public static func maxBackoffLevel(baseInterval: TimeInterval) -> Int {
        guard baseInterval > 0 else { return 0 }
        var level = 0
        while baseInterval * pow(2, Double(level)) < maxBackoffInterval { level += 1 }
        return level
    }

    public static func shouldFetch(_ trigger: FetchTrigger, lastAttempt: Date?, waitUntil: Date?,
                                   backgroundInterval: TimeInterval, tokenSource: Bool = false, now: Date) -> Bool {
        if let wait = waitUntil, now < wait { return false }
        if trigger == .menuOpen && tokenSource { return false }
        guard let last = lastAttempt else { return true }
        let elapsed = now.timeIntervalSince(last)
        switch trigger {
        case .manual: return !tokenSource || elapsed >= manualTokenAge
        case .timer: return elapsed >= backgroundInterval - timerSlack
        case .launch: return elapsed >= backgroundInterval
        case .menuOpen: return elapsed >= min(backgroundInterval, menuOpenAge)
        }
    }
}

/// One account's 429 backoff: each 429 raises the level (doubling its background interval, up to an
/// hour); 3 good reads in a row lower it by one. Any failed read restarts the count.
public struct FetchBackoff: Codable, Equatable, Sendable {
    public static let successesToStepDown = 3
    public var level: Int
    /// Good reads in a row since the last level change or failure.
    public var successes: Int

    public init(level: Int = 0, successes: Int = 0) {
        self.level = level
        self.successes = successes
    }

    public mutating func rateLimited(baseInterval: TimeInterval) {
        level = min(level + 1, FetchPolicy.maxBackoffLevel(baseInterval: baseInterval))
        successes = 0
    }

    public mutating func succeeded() {
        guard level > 0 else { successes = 0; return }
        successes += 1
        if successes >= FetchBackoff.successesToStepDown {
            level -= 1
            successes = 0
        }
    }

    public mutating func failed() { successes = 0 }
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

/// Saved to disk so a relaunch neither fetches inside a wait nor forgets the backoff. Keys added after
/// 0.3.1 are optional: a file written by an earlier version still loads.
public struct PersistedAccountState: Codable, Equatable, Sendable {
    public var snapshot: UsageSnapshot?
    public var lastSuccess: Date?
    public var lastAttempt: Date?
    public var waitUntil: Date?
    public var email: String?
    public var backoff: FetchBackoff?
    /// The route the last fetch used, so the Claude app on its token keeps the token rules after a relaunch.
    public var lastRoute: String?

    public init(snapshot: UsageSnapshot?, lastSuccess: Date?, lastAttempt: Date?, waitUntil: Date?, email: String?,
                backoff: FetchBackoff? = nil, lastRoute: String? = nil) {
        self.snapshot = snapshot; self.lastSuccess = lastSuccess; self.lastAttempt = lastAttempt
        self.waitUntil = waitUntil; self.email = email; self.backoff = backoff; self.lastRoute = lastRoute
    }

    /// A wait saved by 0.3.1 or earlier (no backoff saved) ends exactly on the Retry-After boundary. Count
    /// it as one 429: add the jitter and raise the backoff level. Returns whether it changed anything.
    public mutating func upgradeEarlierWait(baseInterval: TimeInterval, jitter: TimeInterval, now: Date) -> Bool {
        guard backoff == nil, let wait = waitUntil, wait > now else { return false }
        waitUntil = wait.addingTimeInterval(jitter)
        var b = FetchBackoff()
        b.rateLimited(baseInterval: baseInterval)
        backoff = b
        return true
    }
}
