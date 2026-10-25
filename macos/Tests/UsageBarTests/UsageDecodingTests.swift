import XCTest
@testable import UsageBarCore

final class UsageDecodingTests: XCTestCase {
    // Shape from the spec, values made up.
    let fixture = """
    {
      "five_hour":  {"utilization": 33.0, "resets_at": "2026-03-02T08:20:00.393827+00:00"},
      "seven_day":  {"utilization": 77.0, "resets_at": "2026-03-04T15:00:00.393846+00:00"},
      "seven_day_opus": null,
      "seven_day_oauth_apps": null,
      "extra_usage": {"is_enabled": false, "monthly_limit": 10000, "used_credits": 1250.0,
                      "utilization": 12.5, "currency": "AUD", "decimal_places": 2,
                      "disabled_reason": "out_of_credits"},
      "limits": [
        {"kind": "session", "group": "session", "percent": 33, "severity": "normal",
         "resets_at": "2026-03-02T08:20:00.393827+00:00", "scope": null, "is_active": false},
        {"kind": "weekly_all", "group": "weekly", "percent": 77, "severity": "critical",
         "resets_at": "2026-03-04T15:00:00.393846+00:00", "scope": null, "is_active": true},
        {"kind": "weekly_scoped", "group": "weekly", "percent": 5, "severity": "normal",
         "resets_at": "2026-03-04T15:00:00+00:00",
         "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}, "is_active": false},
        {"kind": "something_new", "group": "monthly", "percent": 9},
        "not an object"
      ],
      "unknown_future_key": {"anything": [1, 2, 3]}
    }
    """

    func testDecodesLimitsInOrderWithNames() throws {
        let snap = try UsageDecoder.decode(Data(fixture.utf8), fetchedAt: Date())
        XCTAssertEqual(snap.ordered.map(\.name), ["Session", "Weekly", "Fable weekly"])
        XCTAssertEqual(snap.session?.percent, 33)
        XCTAssertEqual(snap.weekly?.percent, 77)
        XCTAssertEqual(snap.scoped.first?.percent, 5)
        XCTAssertEqual(snap.weekly?.severity, "critical")
        XCTAssertEqual(snap.session?.windowMinutes, 300)
        XCTAssertEqual(snap.weekly?.windowMinutes, 10080)
        XCTAssertEqual(snap.scoped.first?.windowMinutes, 10080)
    }

    func testParsesSixDigitFractionalSecondsWithOffset() throws {
        let snap = try UsageDecoder.decode(Data(fixture.utf8), fetchedAt: Date())
        let expected = ISO8601DateFormatter().date(from: "2026-03-02T08:20:00Z")!
        let got = try XCTUnwrap(snap.session?.resetsAt)
        XCTAssertEqual(got.timeIntervalSince1970, expected.timeIntervalSince1970 + 0.393827, accuracy: 0.001)
    }

    func testTwoScopedLimitsWithDistinctModelIdsBothShow() throws {
        let json = """
        {"limits": [
          {"kind":"weekly_scoped","group":"weekly","percent":5,
           "scope":{"model":{"id":"claude-fable-5-1","display_name":"Fable"}}},
          {"kind":"weekly_scoped","group":"weekly","percent":9,
           "scope":{"model":{"id":"claude-opus-5-5","display_name":"Opus"}}}
        ]}
        """
        let snap = try UsageDecoder.decode(Data(json.utf8), fetchedAt: Date())
        XCTAssertEqual(snap.scoped.count, 2)
        XCTAssertEqual(Set(snap.scoped.map(\.name)), ["Fable weekly", "Opus weekly"])
    }

    func testTwoScopedLimitsSameNameDistinctIdsBothShow() throws {
        let json = """
        {"limits": [
          {"kind":"weekly_scoped","group":"weekly","percent":5,
           "scope":{"model":{"id":"id-a","display_name":"Fable"}}},
          {"kind":"weekly_scoped","group":"weekly","percent":9,
           "scope":{"model":{"id":"id-b","display_name":"Fable"}}}
        ]}
        """
        let snap = try UsageDecoder.decode(Data(json.utf8), fetchedAt: Date())
        XCTAssertEqual(snap.scoped.count, 2)
    }

    func testParsesDateWithoutFraction() {
        XCTAssertEqual(ISODate.parse("2026-03-04T15:00:00+00:00")?.timeIntervalSince1970,
                       ISO8601DateFormatter().date(from: "2026-03-04T15:00:00Z")?.timeIntervalSince1970)
    }

    func testDecodesExtraUsage() throws {
        let snap = try UsageDecoder.decode(Data(fixture.utf8), fetchedAt: Date())
        let e = try XCTUnwrap(snap.extra)
        XCTAssertEqual(e.isEnabled, false)
        XCTAssertEqual(e.usedCredits, 1250)
        XCTAssertEqual(e.monthlyLimit, 10000)
        XCTAssertEqual(e.decimalPlaces, 2)
        XCTAssertEqual(Formatters.extraUsageLine(e), "Extra usage A$12.50 of A$100 · off (out of credits)")
    }

    func testFallsBackToFiveHourAndSevenDayWithoutLimits() throws {
        let json = """
        {"five_hour": {"utilization": 12.5, "resets_at": "2026-03-02T08:20:00.1+00:00"},
         "seven_day": {"utilization": 40, "resets_at": "2026-03-04T15:00:00Z"}, "limits": null}
        """
        let snap = try UsageDecoder.decode(Data(json.utf8), fetchedAt: Date())
        XCTAssertEqual(snap.ordered.map(\.name), ["Session", "Weekly"])
        XCTAssertEqual(snap.session?.percent, 12.5)
        XCTAssertEqual(snap.weekly?.percent, 40)
        XCTAssertNotNil(snap.weekly?.resetsAt)
    }

    func testRejectsNonObject() {
        XCTAssertThrowsError(try UsageDecoder.decode(Data("[1,2]".utf8), fetchedAt: Date()))
    }

    func testProfileDecode() {
        let json = """
        {"account": {"uuid": "a", "email": "x@example.com", "display_name": "X"},
         "organization": {"uuid": "o", "name": "Org", "organization_type": "claude_max", "rate_limit_tier": "t"}}
        """
        let p = Profile.decode(Data(json.utf8))
        XCTAssertEqual(p?.email, "x@example.com")
        XCTAssertEqual(p?.organizationType, "claude_max")
        XCTAssertEqual(p?.organizationUuid, "o")
    }

    func testRetryAfterSecondsAndHTTPDate() {
        let now = ISO8601DateFormatter().date(from: "2026-10-25T08:00:00Z")!
        XCTAssertEqual(UsageClient.retryAfter("120", now: now), 120)
        XCTAssertEqual(UsageClient.retryAfter("Sun, 25 Oct 2026 08:05:00 GMT", now: now), 300)
        XCTAssertNil(UsageClient.retryAfter(nil, now: now))
        XCTAssertNil(UsageClient.retryAfter("soon", now: now))
    }

    func testClaudeCodeCredentialPayloadParse() throws {
        let payload = #"{"claudeAiOauth":{"accessToken":"tok-abc","refreshToken":"r","expiresAt":1790000000000,"subscriptionType":"max"},"mcpOAuth":{}}"#
        let t = try ClaudeCodeCredentials.parse(Data((payload + "\n").utf8))
        XCTAssertEqual(t.accessToken, "tok-abc")
        XCTAssertEqual(t.expiresAt?.timeIntervalSince1970, 1_790_000_000)
        XCTAssertEqual(t.subscriptionType, "max")
        XCTAssertThrowsError(try ClaudeCodeCredentials.parse(Data("{}".utf8)))
    }
}
