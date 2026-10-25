import XCTest
@testable import UsageBarCore

final class PaceTests: XCTestCase {
    let now = ISO8601DateFormatter().date(from: "2026-10-25T06:00:00Z")!
    let utc = TimeZone(identifier: "UTC")!

    func testExpectedMatchesCodexBarExample() {
        // 282 of 300 minutes elapsed -> expected 94%.
        let p = Pace.compute(used: 50, resetsAt: now.addingTimeInterval(18 * 60), windowMinutes: 300, now: now)
        XCTAssertEqual(p.expected, 94, accuracy: 0.0001)
        XCTAssertEqual(p.elapsedMinutes, 282, accuracy: 0.0001)
        XCTAssertEqual(p.tickFraction, 0.94, accuracy: 0.0001)
    }

    func testWithinTwoPointsIsOnPace() {
        // 150 of 300 elapsed -> expected 50.
        let reset = now.addingTimeInterval(150 * 60)
        for used in [48.0, 50, 52] {
            let p = Pace.compute(used: used, resetsAt: reset, windowMinutes: 300, now: now)
            XCTAssertEqual(p.status, .onPace, "used \(used)")
            XCTAssertEqual(p.line(now: now, timeZone: utc), "On pace · lasts until reset")
        }
        // 2.4 points shows as "2", so it is still on pace; 2.6 shows as "3".
        XCTAssertEqual(Pace.compute(used: 52.4, resetsAt: reset, windowMinutes: 300, now: now).status, .onPace)
        XCTAssertEqual(Pace.compute(used: 47.6, resetsAt: reset, windowMinutes: 300, now: now).status, .onPace)
        XCTAssertEqual(Pace.compute(used: 47.4, resetsAt: reset, windowMinutes: 300, now: now).status, .under)
        XCTAssertEqual(Pace.compute(used: 52.6, resetsAt: reset, windowMinutes: 300, now: now).status, .over)
    }

    func testUnderPaceText() {
        let p = Pace.compute(used: 20, resetsAt: now.addingTimeInterval(150 * 60), windowMinutes: 300, now: now)
        XCTAssertEqual(p.delta, -30, accuracy: 0.001)
        XCTAssertEqual(p.line(now: now, timeZone: utc), "30% under pace · lasts until reset")
        XCTAssertFalse(p.isOver)
    }

    func testOverPaceRunsOutBeforeReset() {
        // Weekly: 60% of 10080 min elapsed (6048), 72% used -> 12 over.
        let reset = now.addingTimeInterval((10080 - 6048) * 60)
        let p = Pace.compute(used: 72, resetsAt: reset, windowMinutes: 10080, now: now)
        XCTAssertEqual(p.delta, 12, accuracy: 0.001)
        // rate = 72/6048 %/min; 28% left -> 2352 min = 39.2 h after now.
        let runOut = try! XCTUnwrap(p.runOut)
        XCTAssertEqual(runOut.timeIntervalSince(now), 2352 * 60, accuracy: 1)
        // now 06:00Z Sun + 39h12m = Mon 21:12Z -> rounded to 21:10.
        XCTAssertEqual(p.line(now: now, timeZone: utc), "12% over pace · runs out around Mon 9:10 pm")
        XCTAssertTrue(p.isOver)
    }

    func testOverPaceAlwaysRunsOutBeforeReset() {
        // With a linear rate, used > expected implies the run-out lands before the reset.
        for (elapsed, used) in [(100.0, 40.0), (200.0, 70.0), (280.0, 97.0), (10.0, 6.0)] {
            let reset = now.addingTimeInterval((300 - elapsed) * 60)
            let p = Pace.compute(used: used, resetsAt: reset, windowMinutes: 300, now: now)
            XCTAssertEqual(p.status, .over, "\(elapsed)/\(used)")
            let r = try! XCTUnwrap(p.runOut)
            XCTAssertLessThan(r, reset)
            XCTAssertTrue(p.line(now: now, timeZone: utc).contains("runs out around"))
        }
    }

    func testLimitReachedWhenAtHundred() {
        let p = Pace.compute(used: 100, resetsAt: now.addingTimeInterval(60 * 60), windowMinutes: 300, now: now)
        XCTAssertEqual(p.line(now: now, timeZone: utc), "20% over pace · limit reached")
    }

}

final class FormattingTests: XCTestCase {
    let now = ISO8601DateFormatter().date(from: "2026-10-25T06:00:00Z")!   // Sunday
    let brisbane = TimeZone(identifier: "Australia/Brisbane")!             // UTC+10, no DST

    func testResetUnder24HoursIsRelative() {
        XCTAssertEqual(Formatters.resetPhrase(now.addingTimeInterval(130 * 60), now: now), "in 2h 10m")
        XCTAssertEqual(Formatters.resetPhrase(now.addingTimeInterval(129 * 60 + 30), now: now), "in 2h 10m")
        XCTAssertEqual(Formatters.resetPhrase(now.addingTimeInterval(45 * 60), now: now), "in 45m")
        XCTAssertEqual(Formatters.resetPhrase(now.addingTimeInterval(3 * 3600), now: now), "in 3h")
        XCTAssertEqual(Formatters.resetPhrase(now.addingTimeInterval(23 * 3600 + 59 * 60), now: now), "in 23h 59m")
        XCTAssertEqual(Formatters.resetPhrase(now.addingTimeInterval(-5), now: now), "now")
    }

    func testResetFromOneDayIsWeekdayAndTimeInLocalZone() {
        // Thu 29 Oct 2026 09:00 Brisbane = Wed 28 Oct 23:00Z.
        let thu9 = ISO8601DateFormatter().date(from: "2026-10-28T23:00:00Z")!
        XCTAssertEqual(Formatters.resetPhrase(thu9, now: now, timeZone: brisbane), "Thu 9:00 am")
        let mon2pm = ISO8601DateFormatter().date(from: "2026-10-26T04:00:00Z")!   // Mon 14:00 Brisbane
        XCTAssertEqual(Formatters.resetPhrase(mon2pm.addingTimeInterval(86400 * 7), now: now, timeZone: brisbane),
                       "Mon 2:00 pm")
        XCTAssertEqual(Formatters.resetPhrase(now.addingTimeInterval(24 * 3600), now: now, timeZone: brisbane),
                       "Mon 4:00 pm")
        // The API sends e.g. 14:59:59.6Z for a 15:00Z reset: round to the nearest minute, don't truncate.
        let justBefore = ISO8601DateFormatter().date(from: "2026-10-28T22:59:59Z")!.addingTimeInterval(0.6)
        XCTAssertEqual(Formatters.resetPhrase(justBefore, now: now, timeZone: brisbane), "Thu 9:00 am")
        let justAfter = ISO8601DateFormatter().date(from: "2026-10-28T23:00:20Z")!
        XCTAssertEqual(Formatters.resetPhrase(justAfter, now: now, timeZone: brisbane), "Thu 9:00 am")
    }

    func testMoney() {
        XCTAssertEqual(Formatters.money(6.2), "A$6.20")
        XCTAssertEqual(Formatters.money(41.8), "A$41.80")
        XCTAssertEqual(Formatters.money(214.4), "A$214")
        XCTAssertEqual(Formatters.money(1050.2), "A$1,050")
        XCTAssertEqual(Formatters.money(0), "A$0.00")
    }

    func testTokens() {
        XCTAssertEqual(Formatters.tokens(260_000_000), "260M")
        XCTAssertEqual(Formatters.tokens(1_400_000_000), "1.4B")
        XCTAssertEqual(Formatters.tokens(9_000_000), "9M")
        XCTAssertEqual(Formatters.tokens(9_240_000), "9.2M")
        XCTAssertEqual(Formatters.tokens(86_000_000), "86M")
        XCTAssertEqual(Formatters.tokens(950), "950")
    }

    func testUpdatedAgo() {
        XCTAssertEqual(Formatters.updatedAgo(now.addingTimeInterval(-20), now: now), "Updated just now")
        XCTAssertEqual(Formatters.updatedAgo(now.addingTimeInterval(-125), now: now), "Updated 2 min ago")
        XCTAssertEqual(Formatters.updatedAgo(nil, now: now), "Not updated yet")
    }

    func testExtraUsageEnabledHasNoOffSuffix() {
        let e = ExtraUsage(isEnabled: true, monthlyLimit: 10000, usedCredits: 1250, utilization: 12.5,
                           currency: "AUD", decimalPlaces: 2, disabledReason: nil)
        XCTAssertEqual(Formatters.extraUsageLine(e), "Extra usage A$12.50 of A$100")
    }
}
