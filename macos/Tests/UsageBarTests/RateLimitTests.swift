import XCTest
@testable import UsageBarCore

final class FetchPolicyTests: XCTestCase {
    let now = ISO8601DateFormatter().date(from: "2026-10-26T06:30:00Z")!

    func should(_ t: FetchTrigger, last: TimeInterval?, waitFor: TimeInterval? = nil,
                interval: TimeInterval = 120) -> Bool {
        FetchPolicy.shouldFetch(t, lastAttempt: last.map { now.addingTimeInterval(-$0) },
                                waitUntil: waitFor.map { now.addingTimeInterval($0) }, backgroundInterval: interval,
                                now: now)
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

    func testMenuOpenChecksATokenSourceOnceTwoMinutesHavePassed() {
        for interval: TimeInterval in [300, 600] {
            XCTAssertFalse(should(.menuOpen, last: 119, interval: interval), "\(interval)")
            XCTAssertTrue(should(.menuOpen, last: 120, interval: interval), "\(interval)")
        }
    }

    func testMenuOpenChecksACookieSourceAtItsIntervalWhenThatIsShorterThanTwoMinutes() {
        XCTAssertFalse(should(.menuOpen, last: 59, interval: 60))
        XCTAssertTrue(should(.menuOpen, last: 60, interval: 60))
        XCTAssertFalse(should(.menuOpen, last: 119, interval: 120))
        XCTAssertTrue(should(.menuOpen, last: 120, interval: 120))
    }

    func testTokenSourcesPollNoFasterThanEveryFiveMinutes() {
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 1, tokenSource: true), 300)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: true), 300)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 5, tokenSource: true), 300)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 10, tokenSource: true), 600)
    }

    func testCookieSourcesPollAtTheChosenInterval() {
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 1, tokenSource: false), 60)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 2, tokenSource: false), 120)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 10, tokenSource: false), 600)
    }

    func testAnIntervalOutsideTheSettingsOptionsCountsAsTwoMinutes() {
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 0, tokenSource: false), 120)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 3, tokenSource: false), 120)
        XCTAssertEqual(FetchPolicy.backgroundInterval(userMinutes: 0, tokenSource: true), 300)
    }

    func testClaudeCodeAndTheClaudeAppOnItsTokenAreTokenSources() {
        XCTAssertTrue(FetchPolicy.isTokenSource(.claudeCode, lastRoute: nil))
        XCTAssertTrue(FetchPolicy.isTokenSource(.claudeDesktop, lastRoute: "token"))
        XCTAssertFalse(FetchPolicy.isTokenSource(.claudeDesktop, lastRoute: "cookie"))
        XCTAssertFalse(FetchPolicy.isTokenSource(.claudeDesktop, lastRoute: nil))
        XCTAssertFalse(FetchPolicy.isTokenSource(.safariSession, lastRoute: "safari"))
        XCTAssertFalse(FetchPolicy.isTokenSource(.safariSession, lastRoute: nil))
    }

    func testClaudeCodeOnTheTwoMinuteSettingWaitsFiveMinutesBetweenTimerFetches() {
        let interval = FetchPolicy.backgroundInterval(
            userMinutes: 2, tokenSource: FetchPolicy.isTokenSource(.claudeCode, lastRoute: nil))
        XCTAssertFalse(should(.timer, last: 120, interval: interval))
        XCTAssertFalse(should(.timer, last: 240, interval: interval))
        XCTAssertTrue(should(.timer, last: 300, interval: interval))
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

    func testCookie429WaitsFiveMinutesWhenRetryAfterIsZero() {
        var report = FetchReport()
        AccountFetcher.applyCookieOutcome(.rateLimited(retryAfter: 0), report: &report,
                                          rejected: .expiredDesktop, now: now)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(300)))
        XCTAssertEqual(report.retryAfter, 0)
    }

    func testCookie429HonoursRetryAfterUpToAnHour() {
        var report = FetchReport()
        AccountFetcher.applyCookieOutcome(.rateLimited(retryAfter: 90), report: &report,
                                          rejected: .expiredDesktop, now: now)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(90)))
        AccountFetcher.applyCookieOutcome(.rateLimited(retryAfter: 7200), report: &report,
                                          rejected: .expiredDesktop, now: now)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(3600)))
    }

    func testOAuth429WithRetryAfterZeroWaitsFiveMinutes() {
        var report = FetchReport()
        AccountFetcher.apply(.rateLimited(retryAfter: 0), report: &report, rejected: .expiredCLI, now: now)
        XCTAssertEqual(report.issue, .rateLimited(until: now.addingTimeInterval(300)))
        XCTAssertEqual(report.retryAfter, 0)
        AccountFetcher.apply(.rateLimited(retryAfter: nil), report: &report, rejected: .expiredCLI, now: now)
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
