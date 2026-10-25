import Foundation

public struct Pace: Equatable, Sendable {
    public enum Status: Equatable, Sendable { case onPace, under, over }

    /// Where usage "should" be if spread evenly over the window, 0–100.
    public var expected: Double
    /// used − expected, in percentage points.
    public var delta: Double
    public var elapsedMinutes: Double
    public var status: Status
    /// Estimated time the limit hits 100% at the current rate; nil unless over pace and before the reset.
    public var runOut: Date?
    public var used: Double

    /// |delta| at or below this many points counts as on pace.
    public static let onPaceBand = 2.0

    public static func compute(used: Double, resetsAt: Date, windowMinutes: Double, now: Date) -> Pace {
        let remaining = resetsAt.timeIntervalSince(now) / 60
        let elapsed = min(max(windowMinutes - remaining, 0), windowMinutes)
        let expected = windowMinutes > 0 ? elapsed / windowMinutes * 100 : 0
        let delta = used - expected

        // Band on the rounded figure the text shows, so "2% under pace" never appears.
        let status: Status
        if abs(delta).rounded() <= onPaceBand { status = .onPace }
        else if delta < 0 { status = .under }
        else { status = .over }

        var runOut: Date?
        if status == .over {
            if used >= 100 {
                runOut = now
            } else if used > 0 {
                let rate = used / max(elapsed, 1)          // percent per minute so far
                let minutesToFull = (100 - used) / rate
                let candidate = now.addingTimeInterval(minutesToFull * 60)
                if candidate < resetsAt { runOut = candidate }
            }
        }
        return Pace(expected: expected, delta: delta, elapsedMinutes: elapsed, status: status, runOut: runOut, used: used)
    }

    /// Pace marker position on the bar, 0–1.
    public var tickFraction: Double { min(max(expected / 100, 0), 1) }

    public var points: Int { Int(abs(delta).rounded()) }

    public func line(now: Date, timeZone: TimeZone = .current) -> String {
        switch status {
        case .onPace:
            return "On pace · lasts until reset"
        case .under:
            return "\(points)% under pace · lasts until reset"
        case .over:
            if used >= 100 { return "\(points)% over pace · limit reached" }
            if let r = runOut {
                return "\(points)% over pace · runs out around \(Formatters.runOutTime(r, now: now, timeZone: timeZone))"
            }
            return "\(points)% over pace · lasts until reset"
        }
    }

    public var isOver: Bool { status == .over }
}
