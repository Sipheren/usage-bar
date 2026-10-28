import XCTest
@testable import UsageBarCore

final class FetchPolicyTests: XCTestCase {
    let now = ISO8601DateFormatter().date(from: "2026-10-26T06:30:00Z")!

    func should(_ t: FetchTrigger, last: TimeInterval?, waitFor: TimeInterval? = nil,
                interval: TimeInterval = 120, token: Bool = false) -> Bool {
        FetchPolicy.shouldFetch(t, lastAttempt: last.map { now.addingTimeInterval(-$0) },
                                waitUntil: waitFor.map { now.addingTimeInterval($0) }, backgroundInterval: interval,
                                tokenSource: token, now: now)
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

    func testRetryAfterZeroOrMissingWaitsFiveMinutesAndLongOnesAreCappedAtAnHour() {
        XCTAssertEqual(FetchPolicy.rateLimitWait(nil), 300)
        XCTAssertEqual(FetchPolicy.rateLimitWait(0), 300)
        XCTAssertEqual(FetchPolicy.rateLimitWait(-5), 300)
        XCTAssertEqual(FetchPolicy.rateLimitWait(30), 30)
        XCTAssertEqual(FetchPolicy.rateLimitWait(3600), 3600)
        XCTAssertEqual(FetchPolicy.rateLimitWait(7200), 3600)
    }

    func testTimerFetchesOnlyOnceTheIntervalHasNearlyPassed() {
        XCTAssertTrue(should(.timer, last: nil))
        XCTAssertFalse(should(.timer, last: 60))
        XCTAssertFalse(should(.timer, last: 80))
        XCTAssertFalse(should(.timer, last: 89))
        // 30 s of slack: the 60 s tick fires a little off, and without it the 2-minute setting fires at 3.
        XCTAssertTrue(should(.timer, last: 90))
        XCTAssertTrue(should(.timer, last: 95))
        XCTAssertTrue(should(.timer, last: 119.5))
    }

    func testTimerAtFiveMinutesSkipsTheEarlierTicks() {
        for elapsed: TimeInterval in [60, 120, 180, 240, 269] {
            XCTAssertFalse(should(.timer, last: elapsed, interval: 300), "\(elapsed)")
        }
        XCTAssertTrue(should(.timer, last: 270, interval: 300))
        XCTAssertTrue(should(.timer, last: 300, interval: 300))
    }

    func testLaunchWaitsForTheWholeIntervalWithNoSlack() {
        XCTAssertFalse(should(.launch, last: 299, interval: 300))
        XCTAssertTrue(should(.launch, last: 300, interval: 300))
    }

    func testMenuOpenNeverFetchesATokenSource() {
        for last: TimeInterval? in [nil, 0, 119, 120, 900, 3600, 86_400] {
            XCTAssertFalse(should(.menuOpen, last: last, interval: 900, token: true), "\(String(describing: last))")
        }
    }

    func testMenuOpenStillChecksACookieSourceOnceTwoMinutesHavePassed() {
        for interval: TimeInterval in [300, 600, 1200] {
            XCTAssertFalse(should(.menuOpen, last: 119, interval: interval), "\(interval)")
            XCTAssertTrue(should(.menuOpen, last: 120, interval: interval), "\(interval)")
        }
        XCTAssertTrue(should(.menuOpen, last: nil, interval: 120))
    }

    func testRefreshNowOnATokenSourceWaitsFiveMinutesAfterTheLastAttempt() {
        XCTAssertTrue(should(.manual, last: nil, interval: 900, token: true))
        XCTAssertFalse(should(.manual, last: 5, interval: 900, token: true))
        XCTAssertFalse(should(.manual, last: 299, interval: 900, token: true))
        XCTAssertTrue(should(.manual, last: 300, interval: 900, token: true))
        XCTAssertFalse(should(.manual, last: 999, waitFor: 60, interval: 900, token: true))
    }

    func testRefreshNowOnACookieSourceStillFetchesOutsideAWait() {
        XCTAssertTrue(should(.manual, last: 1, interval: 120))
        XCTAssertFalse(should(.manual, last: 1, waitFor: 60, interval: 120))
    }

    func testATokenSourceTimerAndLaunchUseTheFifteenMinuteFloor() {
        let interval = FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: true)
        XCTAssertFalse(should(.timer, last: 840, interval: interval, token: true))
        XCTAssertTrue(should(.timer, last: 870, interval: interval, token: true))
        XCTAssertFalse(should(.launch, last: 899, interval: interval, token: true))
        XCTAssertTrue(should(.launch, last: 900, interval: interval, token: true))
        XCTAssertTrue(should(.launch, last: nil, interval: interval, token: true))
    }

    func testMenuOpenChecksACookieSourceAtItsIntervalWhenThatIsShorterThanTwoMinutes() {
        XCTAssertFalse(should(.menuOpen, last: 59, interval: 60))
        XCTAssertTrue(should(.menuOpen, last: 60, interval: 60))
        XCTAssertFalse(should(.menuOpen, last: 119, interval: 120))
        XCTAssertTrue(should(.menuOpen, last: 120, interval: 120))
    }

    func testTokenSourcesPollNoFasterThanEveryFifteenMinutes() {
        XCTAssertEqual(FetchPolicy.tokenMinimumInterval, 900)
        for minutes in [1, 2, 5, 10] {
            XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: minutes, tokenSource: true), 900, "\(minutes)")
        }
    }

    func testCookieSourcesPollAtTheChosenInterval() {
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 1, tokenSource: false), 60)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: false), 120)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 10, tokenSource: false), 600)
    }

    func testAnIntervalOutsideTheSettingsOptionsCountsAsTwoMinutes() {
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 0, tokenSource: false), 120)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 3, tokenSource: false), 120)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 0, tokenSource: true), 900)
    }

    func testClaudeCodeAndTheClaudeAppOnItsTokenAreTokenSources() {
        XCTAssertTrue(FetchPolicy.isTokenSource(.claudeCode, lastRoute: nil))
        XCTAssertTrue(FetchPolicy.isTokenSource(.claudeDesktop, lastRoute: "token"))
        XCTAssertFalse(FetchPolicy.isTokenSource(.claudeDesktop, lastRoute: "cookie"))
        XCTAssertFalse(FetchPolicy.isTokenSource(.claudeDesktop, lastRoute: nil))
        XCTAssertFalse(FetchPolicy.isTokenSource(.safariSession, lastRoute: "safari"))
        XCTAssertFalse(FetchPolicy.isTokenSource(.safariSession, lastRoute: nil))
    }

    func testClaudeCodeOnTheTwoMinuteSettingWaitsFifteenMinutesBetweenTimerFetches() {
        let token = FetchPolicy.isTokenSource(.claudeCode, lastRoute: nil)
        let interval = FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: token)
        for elapsed: TimeInterval in [120, 300, 600, 869] {
            XCTAssertFalse(should(.timer, last: elapsed, interval: interval, token: token), "\(elapsed)")
        }
        XCTAssertTrue(should(.timer, last: 900, interval: interval, token: token))
    }
}

/// A generator that always returns the same value, for the jitter's end points.
struct FixedGenerator: RandomNumberGenerator {
    let value: UInt64
    mutating func next() -> UInt64 { value }
}

/// SplitMix64: a small seeded generator, so the jitter tests are repeatable.
struct SeededGenerator: RandomNumberGenerator {
    var state: UInt64
    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }
}

final class RateLimitJitterTests: XCTestCase {
    func testJitterIsTwoToTenMinutes() {
        XCTAssertEqual(FetchPolicy.rateLimitJitterRange, 120...600)
        var low = FixedGenerator(value: 0)
        XCTAssertEqual(FetchPolicy.rateLimitJitter(using: &low), 120)
        var high = FixedGenerator(value: .max)
        let top = FetchPolicy.rateLimitJitter(using: &high)
        XCTAssertGreaterThan(top, 599)
        XCTAssertLessThanOrEqual(top, 600)
    }

    func testSeededJitterStaysInRangeAndSpreadsOut() {
        var rng = SeededGenerator(state: 42)
        let draws = (0..<2000).map { _ in FetchPolicy.rateLimitJitter(using: &rng) }
        XCTAssertTrue(draws.allSatisfy { (120...600).contains($0) })
        XCTAssertLessThan(draws.min()!, 150)
        XCTAssertGreaterThan(draws.max()!, 570)
    }

    func testTheWaitIsRetryAfterPlusTheJitter() {
        let now = ISO8601DateFormatter().date(from: "2026-10-26T06:30:00Z")!
        var report = FetchReport()
        AccountFetcher.apply(.rateLimited(retryAfter: 3402), report: &report, rejected: .expiredCLI, now: now,
                             jitter: 245)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(3402 + 245)))
        XCTAssertEqual(report.rateLimitJitter, 245)
        AccountFetcher.apply(.rateLimited(retryAfter: 7200), report: &report, rejected: .expiredCLI, now: now,
                             jitter: 600)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(3600 + 600)))
        AccountFetcher.applyCookieOutcome(.rateLimited(retryAfter: 0), report: &report, rejected: .expiredDesktop,
                                          now: now, jitter: 120)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(300 + 120)))
        XCTAssertEqual(report.rateLimitJitter, 120)
    }

    func testAnUnsetJitterIsDrawnFromTheRange() {
        let now = ISO8601DateFormatter().date(from: "2026-10-26T06:30:00Z")!
        for _ in 0..<50 {
            var report = FetchReport()
            AccountFetcher.apply(.rateLimited(retryAfter: 100), report: &report, rejected: .expiredCLI, now: now)
            guard case .rateLimited(let until)? = report.issue, let j = report.rateLimitJitter else {
                return XCTFail("a 429 should be rate limited with a jitter")
            }
            XCTAssertTrue((120...600).contains(j))
            XCTAssertEqual(until, now.addingTimeInterval(100 + j))
        }
    }
}

final class BackoffTests: XCTestCase {
    func testEach429DoublesATokenSourceUpToAnHour() {
        var b = FetchBackoff()
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: true, backoffLevel: b.level), 900)
        b.rateLimited(baseInterval: 900)
        XCTAssertEqual(b.level, 1)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: true, backoffLevel: b.level), 1800)
        b.rateLimited(baseInterval: 900)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: true, backoffLevel: b.level), 3600)
        b.rateLimited(baseInterval: 900)
        XCTAssertEqual(b.level, 2, "no level past the hour, so one step down is felt")
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: true, backoffLevel: b.level), 3600)
    }

    func testEach429DoublesACookieSourceUpToAnHour() {
        var b = FetchBackoff()
        var seen: [TimeInterval] = []
        for _ in 0..<8 {
            b.rateLimited(baseInterval: 120)
            seen.append(FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: false, backoffLevel: b.level))
        }
        XCTAssertEqual(seen, [240, 480, 960, 1920, 3600, 3600, 3600, 3600])
        XCTAssertEqual(b.level, 5)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 10, tokenSource: false, backoffLevel: 3), 3600)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 10, tokenSource: false, backoffLevel: 1), 1200)
    }

    func testThreeGoodReadsStepBackOneLevel() {
        var b = FetchBackoff(level: 2, successes: 0)
        b.succeeded(); b.succeeded()
        XCTAssertEqual(b.level, 2)
        b.succeeded()
        XCTAssertEqual(b.level, 1)
        XCTAssertEqual(b.successes, 0)
        b.succeeded(); b.succeeded(); b.succeeded()
        XCTAssertEqual(b.level, 0)
        b.succeeded(); b.succeeded(); b.succeeded()
        XCTAssertEqual(b, FetchBackoff(level: 0, successes: 0))
    }

    func testA429OrAnyOtherFailureRestartsTheCount() {
        var b = FetchBackoff(level: 1, successes: 0)
        b.succeeded(); b.succeeded()
        b.failed()
        b.succeeded(); b.succeeded()
        XCTAssertEqual(b.level, 1, "the failure broke the run of good reads")
        b.rateLimited(baseInterval: 900)
        XCTAssertEqual(b, FetchBackoff(level: 2, successes: 0))
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

    func testTheWaitBackoffAndRouteSurviveARelaunch() throws {
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        let state = PersistedAccountState(snapshot: nil, lastSuccess: now.addingTimeInterval(-300),
                                          lastAttempt: now, waitUntil: now.addingTimeInterval(3647), email: nil,
                                          backoff: FetchBackoff(level: 2, successes: 1), lastRoute: "token")
        let saved = try JSONEncoder().encode(["id": state])
        let back = try JSONDecoder().decode([String: PersistedAccountState].self, from: saved)["id"]
        XCTAssertEqual(back, state)
        XCTAssertEqual(back?.backoff, FetchBackoff(level: 2, successes: 1))
        XCTAssertEqual(back?.lastRoute, "token")
        // And the restored wait still blocks every trigger.
        for t in [FetchTrigger.launch, .timer, .menuOpen, .manual] {
            XCTAssertFalse(FetchPolicy.shouldFetch(t, lastAttempt: back?.lastAttempt, waitUntil: back?.waitUntil,
                                                   backgroundInterval: 900, tokenSource: true,
                                                   now: now.addingTimeInterval(3600)), "\(t)")
        }
    }

    func testStateSavedByAnEarlierVersionStillLoads() throws {
        // 0.3.1 wrote only these keys (dates as seconds since 2001); one missing key must not drop the file.
        let json = #"{"a":{"lastAttempt":783316699.5,"lastSuccess":783316399.3,"waitUntil":783320101.5},"b":{"lastAttempt":783317359.1}}"#
        let saved = try JSONDecoder().decode([String: PersistedAccountState].self, from: Data(json.utf8))
        XCTAssertEqual(saved.count, 2)
        XCTAssertNil(saved["a"]?.backoff)
        XCTAssertNil(saved["a"]?.lastRoute)
        XCTAssertEqual(saved["a"]?.waitUntil, Date(timeIntervalSinceReferenceDate: 783320101.5))
    }

    func testAWaitSavedByAnEarlierVersionGetsAJitterAndCountsAsOne429() {
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        let until = now.addingTimeInterval(2700)
        var st = PersistedAccountState(snapshot: nil, lastSuccess: nil, lastAttempt: now.addingTimeInterval(-700),
                                       waitUntil: until, email: nil)
        XCTAssertTrue(st.upgradeEarlierWait(baseInterval: 900, jitter: 300, now: now))
        XCTAssertEqual(st.waitUntil, until.addingTimeInterval(300))
        XCTAssertEqual(st.backoff, FetchBackoff(level: 1, successes: 0))
        // Once upgraded (the backoff is saved with it), it is never moved again.
        XCTAssertFalse(st.upgradeEarlierWait(baseInterval: 900, jitter: 300, now: now))
        XCTAssertEqual(st.waitUntil, until.addingTimeInterval(300))
    }

    func testAPassedWaitFromAnEarlierVersionIsLeftAlone() {
        let now = Date(timeIntervalSince1970: 1_790_000_000)
        var passed = PersistedAccountState(snapshot: nil, lastSuccess: nil, lastAttempt: nil,
                                           waitUntil: now.addingTimeInterval(-1), email: nil)
        XCTAssertFalse(passed.upgradeEarlierWait(baseInterval: 900, jitter: 300, now: now))
        XCTAssertEqual(passed.waitUntil, now.addingTimeInterval(-1))
        XCTAssertNil(passed.backoff)
        var none = PersistedAccountState(snapshot: nil, lastSuccess: nil, lastAttempt: nil, waitUntil: nil, email: nil)
        XCTAssertFalse(none.upgradeEarlierWait(baseInterval: 900, jitter: 300, now: now))
    }
}

final class RateLimitResponseTests: XCTestCase {
    let now = ISO8601DateFormatter().date(from: "2026-10-26T06:30:00Z")!
    let url = URL(string: "https://claude.ai/api/organizations")!

    func response(_ status: Int, _ headers: [String: String] = [:]) -> HTTPURLResponse {
        HTTPURLResponse(url: url, statusCode: status, httpVersion: nil, headerFields: headers)!
    }

    func testClaudeAi429IsRateLimitedWithItsRetryAfter() {
        guard case .rateLimited(let zero)? = ClaudeAiCookieClient.blockedOutcome(
            response(429, ["Retry-After": "0"]), body: Data("{}".utf8), now: now) else {
            return XCTFail("429 should be rate limited")
        }
        XCTAssertEqual(zero, 0)
        guard case .rateLimited(let seconds)? = ClaudeAiCookieClient.blockedOutcome(
            response(429, ["Retry-After": "120"]), body: Data(), now: now) else {
            return XCTFail("429 should be rate limited")
        }
        XCTAssertEqual(seconds, 120)
        guard case .rateLimited(let missing)? = ClaudeAiCookieClient.blockedOutcome(
            response(429), body: Data(), now: now) else {
            return XCTFail("429 should be rate limited")
        }
        XCTAssertNil(missing)
    }

    func testCloudflareIsCheckedBeforeA429() {
        let page = Data("<title>Just a moment...</title>".utf8)
        guard case .cloudflare? = ClaudeAiCookieClient.blockedOutcome(response(429), body: page, now: now) else {
            return XCTFail("a Cloudflare page on a 429 is a challenge")
        }
    }

    func testOtherStatusesAreLeftToTheCaller() {
        for status in [200, 401, 403, 500] {
            XCTAssertNil(ClaudeAiCookieClient.blockedOutcome(response(status), body: Data("{}".utf8), now: now),
                         "\(status)")
        }
    }

    // These pin the jitter to 0 to check the Retry-After part of the wait on its own.
    func testCookie429WaitsFiveMinutesWhenRetryAfterIsZero() {
        var report = FetchReport()
        AccountFetcher.applyCookieOutcome(.rateLimited(retryAfter: 0), report: &report,
                                          rejected: .expiredDesktop, now: now, jitter: 0)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(300)))
        XCTAssertEqual(report.retryAfter, 0)
    }

    func testCookie429HonoursRetryAfterUpToAnHour() {
        var report = FetchReport()
        AccountFetcher.applyCookieOutcome(.rateLimited(retryAfter: 90), report: &report,
                                          rejected: .expiredDesktop, now: now, jitter: 0)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(90)))
        AccountFetcher.applyCookieOutcome(.rateLimited(retryAfter: 7200), report: &report,
                                          rejected: .expiredDesktop, now: now, jitter: 0)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(3600)))
    }

    func testOAuth429WithRetryAfterZeroWaitsFiveMinutes() {
        var report = FetchReport()
        AccountFetcher.apply(.rateLimited(retryAfter: 0), report: &report, rejected: .expiredCLI, now: now, jitter: 0)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(300)))
        XCTAssertEqual(report.retryAfter, 0)
        AccountFetcher.apply(.rateLimited(retryAfter: nil), report: &report, rejected: .expiredCLI, now: now, jitter: 0)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(300)))
    }

    func testClaudeAppCookieResultsThatStandAndThoseThatTryTheToken() {
        let snap = UsageSnapshot(limits: [], extra: nil, fetchedAt: now)
        let stand: [CookieUsageOutcome] = [.ok(snap, org: ClaudeAiOrg(uuid: "w", name: nil)),
                                           .rateLimited(retryAfter: 0), .network("offline"), .signedInToPersonal]
        for outcome in stand {
            XCTAssertTrue(AccountFetcher.desktopCookieResultStands(outcome), "\(outcome)")
        }
        let tryToken: [CookieUsageOutcome] = [.unauthorized(401), .noSession, .noOrganizations, .cloudflare,
                                              .scopeError, .http(500), .undecodable]
        for outcome in tryToken {
            XCTAssertFalse(AccountFetcher.desktopCookieResultStands(outcome), "\(outcome)")
        }
    }
}
