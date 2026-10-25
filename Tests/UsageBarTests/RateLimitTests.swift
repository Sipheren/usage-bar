import XCTest
@testable import UsageBarCore

final class FetchPolicyTests: XCTestCase {
    let now = ISO8601DateFormatter().date(from: "2026-10-26T06:30:00Z")!
    let interval: TimeInterval = 120

    func should(_ t: FetchTrigger, last: TimeInterval?, waitFor: TimeInterval? = nil) -> Bool {
        FetchPolicy.shouldFetch(t, lastAttempt: last.map { now.addingTimeInterval(-$0) },
                                waitUntil: waitFor.map { now.addingTimeInterval($0) }, interval: interval, now: now)
    }

    func testLaunchSkipsAnAccountFetchedWithinTheInterval() {
        XCTAssertFalse(should(.launch, last: 30))
        XCTAssertTrue(should(.launch, last: 121))
        XCTAssertTrue(should(.launch, last: nil))
    }

    func testMenuOpenUsesTheRefreshIntervalNotSixtySeconds() {
        XCTAssertFalse(should(.menuOpen, last: 90))
        XCTAssertTrue(should(.menuOpen, last: 120))
    }

    func testManualRefreshForcesUnlessInsideRetryAfter() {
        XCTAssertTrue(should(.manual, last: 5))
        XCTAssertFalse(should(.manual, last: 5, waitFor: 60))
    }

    func testNothingFetchesInsideARetryAfterWindow() {
        for t in [FetchTrigger.launch, .timer, .menuOpen, .manual] {
            XCTAssertFalse(should(t, last: 999, waitFor: 1), "\(t)")
        }
        XCTAssertTrue(should(.timer, last: 999, waitFor: -1))   // window passed
    }
}

final class RateLimitPresentationTests: XCTestCase {
    let utc = TimeZone(identifier: "UTC")!
    let until = ISO8601DateFormatter().date(from: "2026-10-26T06:40:00Z")!

    func testRateLimitWithDataKeepsNumbersAndNotesItInTheHeader() {
        let p = AccountPresentation(issue: .rateLimited(until: until), hasData: true, timeZone: utc)
        XCTAssertEqual(p.headerNote, "Paused by rate limit · retrying 6:40 am")
        XCTAssertNil(p.inlineIssue)
        XCTAssertFalse(p.greysValues)
    }

    func testRateLimitWithoutDataIsShownAsAnError() {
        let p = AccountPresentation(issue: .rateLimited(until: until), hasData: false, timeZone: utc)
        XCTAssertNil(p.headerNote)
        XCTAssertEqual(p.inlineIssue, .rateLimited(until: until))
        XCTAssertEqual(AccountIssue.rateLimited(until: until).message(timeZone: utc), "Rate limited — retrying after 6:40 am")
    }

    func testOtherIssuesStillShowInlineAndGreyOldNumbers() {
        let p = AccountPresentation(issue: .network("offline"), hasData: true, timeZone: utc)
        XCTAssertNil(p.headerNote)
        XCTAssertEqual(p.inlineIssue, .network("offline"))
        XCTAssertTrue(p.greysValues)
    }

    func testNoIssueShowsNothing() {
        let p = AccountPresentation(issue: nil, hasData: true, timeZone: utc)
        XCTAssertNil(p.headerNote)
        XCTAssertNil(p.inlineIssue)
        XCTAssertFalse(p.greysValues)
    }
}

final class PersistedStateTests: XCTestCase {
    func testSnapshotRoundTripsThroughJSON() throws {
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        let snap = UsageSnapshot(limits: [
            UsageLimit(kind: .session, percent: 20, resetsAt: now),
            UsageLimit(kind: .scoped(id: "m", name: "Model"), percent: 3, resetsAt: nil),
        ], extra: ExtraUsage(isEnabled: true, monthlyLimit: 5000, usedCredits: 100, utilization: 2,
                             currency: "AUD", decimalPlaces: 2, disabledReason: nil), fetchedAt: now)
        let state = PersistedAccountState(snapshot: snap, lastSuccess: now, lastAttempt: now,
                                          waitUntil: now.addingTimeInterval(60), email: "personal@example.com")
        let back = try JSONDecoder().decode(PersistedAccountState.self, from: JSONEncoder().encode(state))
        XCTAssertEqual(back, state)
    }
}
