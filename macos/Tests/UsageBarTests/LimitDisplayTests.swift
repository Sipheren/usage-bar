import XCTest
@testable import UsageBarCore

final class LimitDisplayTests: XCTestCase {
    let now = ISO8601DateFormatter().date(from: "2026-10-25T06:00:00Z")!
    let utc = TimeZone(identifier: "UTC")!

    func testLimitWithResetShowsPercentResetAndPace() {
        let l = UsageLimit(kind: .session, percent: 20, resetsAt: now.addingTimeInterval(150 * 60))
        let d = LimitDisplay(l, now: now, timeZone: utc)
        XCTAssertEqual(d.percentText, "20%")
        XCTAssertEqual(d.compactTrailing, "resets in 2h 30m")
        XCTAssertEqual(d.largeTrailing, "Resets in 2h 30m")
        XCTAssertEqual(d.paceLine, "30% under pace · lasts until reset")
        XCTAssertNotNil(d.tickFraction)
    }

    func testUnstartedSessionSaysStartsOnFirstUseAndHasNoPace() {
        let l = UsageLimit(kind: .session, percent: 0, resetsAt: nil)
        let d = LimitDisplay(l, now: now, timeZone: utc)
        XCTAssertEqual(d.percentText, "0%")
        XCTAssertEqual(d.compactTrailing, "starts on first use")
        XCTAssertEqual(d.largeTrailing, "Starts on first use")
        XCTAssertNil(d.paceLine)
        XCTAssertNil(d.tickFraction)
        XCTAssertFalse(d.isOverPace)
    }

    func testNoResetButSomeUsageShowsJustThePercent() {
        let d = LimitDisplay(UsageLimit(kind: .weekly, percent: 12, resetsAt: nil), now: now, timeZone: utc)
        XCTAssertNil(d.compactTrailing)
        XCTAssertNil(d.largeTrailing)
        XCTAssertNil(d.paceLine)
    }

    func testScopedLimitUsesTheSamePaceLogicAsTheOthers() {
        // 4000 of 10080 minutes left -> expected 60.3%; 12% used -> 48 under.
        let l = UsageLimit(kind: .scoped(id: "fable", name: "Fable"), percent: 12, resetsAt: now.addingTimeInterval(4000 * 60))
        let d = LimitDisplay(l, now: now, timeZone: utc)
        XCTAssertEqual(d.paceLine, "48% under pace · lasts until reset")
        XCTAssertFalse(d.paceLine?.contains("left") ?? true)
    }

    func testOverPaceFlag() {
        let reset = now.addingTimeInterval((10080 - 6048) * 60)
        let d = LimitDisplay(UsageLimit(kind: .weekly, percent: 72, resetsAt: reset), now: now, timeZone: utc)
        XCTAssertTrue(d.isOverPace)
    }
}

final class LabelAndCurrencyTests: XCTestCase {
    func testStyleTitlesAndOrder() {
        XCTAssertEqual(MenuBarStyle.allCases.map(\.title), ["Bars", "Percentages", "Compact", "Icon only"])
    }

    func testLayoutTitles() {
        XCTAssertEqual(DropdownLayout.allCases.map(\.title), ["All accounts", "One at a time"])
    }

}
