import Foundation

/// Everything the dropdown shows for one limit row, in one place so both layouts agree.
/// Every limit, scoped ones included, uses the same pace logic.
public struct LimitDisplay {
    public var percentText: String
    /// Layout A, after the bold percent: "resets in 2h 10m", "starts on first use", or nil.
    public var compactTrailing: String?
    /// Layout B, right-hand side: "Resets in 2h 10m", "Starts on first use", or nil.
    public var largeTrailing: String?
    /// Pace sentence; nil when the window hasn't started (no reset time), so no pace can be computed.
    public var paceLine: String?
    /// Pace marker on the bar, 0–1; nil when there's no pace.
    public var tickFraction: Double?
    public var isOverPace: Bool

    public init(_ limit: UsageLimit, now: Date, timeZone: TimeZone = .current) {
        let pct = Int(max(0, limit.percent).rounded())
        percentText = "\(pct)%"
        if let reset = limit.resetsAt {
            let phrase = Formatters.resetPhrase(reset, now: now, timeZone: timeZone)
            compactTrailing = "resets \(phrase)"
            largeTrailing = "Resets \(phrase)"
            let pace = Pace.compute(used: limit.percent, resetsAt: reset, windowMinutes: limit.windowMinutes, now: now)
            paceLine = pace.line(now: now, timeZone: timeZone)
            tickFraction = pace.tickFraction
            isOverPace = pace.isOver
        } else {
            // No reset time: the window starts with the first request. Nothing to pace against.
            compactTrailing = pct == 0 ? "starts on first use" : nil
            largeTrailing = pct == 0 ? "Starts on first use" : nil
            paceLine = nil
            tickFraction = nil
            isOverPace = false
        }
    }
}
