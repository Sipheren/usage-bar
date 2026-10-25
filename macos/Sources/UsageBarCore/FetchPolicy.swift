import Foundation

public enum FetchTrigger: Sendable { case launch, timer, menuOpen, manual }

/// When an account may call its usage endpoint. Every call costs rate-limit budget, so:
/// nothing fetches inside a Retry-After window; a launch or a menu open fetches only when the last
/// attempt is at least one refresh interval old; the timer and Refresh Now always may.
public enum FetchPolicy {
    public static func shouldFetch(_ trigger: FetchTrigger, lastAttempt: Date?, waitUntil: Date?,
                                   interval: TimeInterval, now: Date) -> Bool {
        if let wait = waitUntil, now < wait { return false }
        switch trigger {
        case .timer, .manual:
            return true
        case .launch, .menuOpen:
            guard let last = lastAttempt else { return true }
            return now.timeIntervalSince(last) >= interval
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
