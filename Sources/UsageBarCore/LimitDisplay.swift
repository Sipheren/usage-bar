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

extension Formatters {
    /// "AUD · US$1 = A$1.42 · updated 26 Oct", or "… · built-in rate" before the first fetch.
    public static func currencyLine(_ rate: ExchangeRate, timeZone: TimeZone = .current) -> String {
        let symbol = currencySymbol(rate.currency)
        let base = "\(rate.currency) · US$1 = \(symbol)\(String(format: "%.2f", rate.rate))"
        if rate.isFallback { return base + " · built-in rate" }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = timeZone
        f.dateFormat = "d MMM"
        return base + " · updated " + f.string(from: rate.asOf)
    }
}
