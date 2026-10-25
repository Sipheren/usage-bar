import Foundation
import UsageBarCore

/// Built-in sample data for demo mode (`--demo` or `USAGEBAR_DEMO=1`): the mockup's numbers and
/// made-up identities. Used for README screenshots, so it must never contain real data.
enum DemoData {
    static let personalEmail = "personal@example.com"
    static let workOrganization = "Example Co"
    static let rates = ExchangeRates(rates: ["USD": 1, "AUD": 1.42, "EUR": 0.92, "GBP": 0.79, "NZD": 1.66,
                                             "CAD": 1.37, "JPY": 151.3], asOf: Date(), fetchedAt: Date())

    /// A limit `used` percent through a window, `expected` percent of the way through it.
    private static func limit(_ kind: LimitKind, used: Double, expected: Double, now: Date) -> UsageLimit {
        let window: Double = kind == .session ? 300 : 10080
        let minutesLeft = window * (1 - expected / 100)
        return UsageLimit(kind: kind, percent: used, resetsAt: now.addingTimeInterval(minutesLeft * 60))
    }

    static func personalSnapshot(now: Date) -> UsageSnapshot {
        UsageSnapshot(limits: [
            limit(.session, used: 20, expected: 27, now: now),
            limit(.weekly, used: 60, expected: 71, now: now),
            limit(.scoped(id: "demo-model", name: "Fable"), used: 12, expected: 71, now: now),
        ], extra: ExtraUsage(isEnabled: true, monthlyLimit: 5000, usedCredits: 1250, utilization: 25,
                             currency: "AUD", decimalPlaces: 2, disabledReason: nil), fetchedAt: now)
    }

    static func workSnapshot(now: Date) -> UsageSnapshot {
        UsageSnapshot(limits: [
            limit(.session, used: 45, expected: 57, now: now),
            limit(.weekly, used: 72, expected: 60, now: now),
            limit(.scoped(id: "demo-model", name: "Fable"), used: 4, expected: 60, now: now),
        ], extra: nil, fetchedAt: now)
    }

    /// Mockup figures in AUD (A$6.20 today, A$214 over 30 days), stored as USD like real summaries.
    static func personalCost(audPerUSD rate: Double) -> CostSummary {
        let shape: [Double] = [0.10, 0, 0.22, 0.35, 0.15, 0, 0.05, 0.48, 0.62, 0.30, 0.12, 0, 0, 0.27, 0.55,
                               0.71, 0.40, 0.18, 0.09, 0, 0.33, 0.95, 0.58, 0.21, 0.14, 0, 0.36, 0.44, 0.26, 0.30]
        let thirtyDayAUD = 214.0, todayAUD = 6.20
        let total = shape.reduce(0, +)
        var s = CostSummary()
        s.dailyUSD = shape.map { $0 / total * thirtyDayAUD / rate }
        s.dailyUSD[29] = todayAUD / rate
        s.todayUSD = todayAUD / rate
        s.last30USD = thirtyDayAUD / rate
        s.todayTokens = 9_000_000
        s.last30Tokens = 260_000_000
        s.topModel = "claude-opus-5-5"
        return s
    }
}
