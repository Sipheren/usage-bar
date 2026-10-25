import XCTest
@testable import UsageBarCore

final class JSONValueTests: XCTestCase {
    func testRoundTripKeepsKeyOrderAndNumberSpelling() throws {
        let text = #"{"z": 1, "a": 33.0, "m": [1e3, -0.50, 7], "s": "x\"y\\z\né😀/", "t": true, "n": null, "o": {}}"#
        let v = try JSONValue.parse(text)
        guard case .object(let members) = v else { return XCTFail("not an object") }
        XCTAssertEqual(members.map(\.key), ["z", "a", "m", "s", "t", "n", "o"])
        XCTAssertEqual(v["a"], .number("33.0"))
        XCTAssertEqual(v["m"], .array([.number("1e3"), .number("-0.50"), .number("7")]))
        XCTAssertEqual(v["s"]?.stringValue, "x\"y\\z\né😀/")
        XCTAssertEqual(try JSONValue.parse(v.serialized()), v)
        XCTAssertTrue(v.serialized().contains("\"a\": 33.0"))
    }

    func testRejectsMalformedJSON() {
        for bad in ["{", "[1,]", "{\"a\" 1}", "tru", "01x", "\"open"] {
            XCTAssertThrowsError(try JSONValue.parse(bad), bad)
        }
    }
}

final class FixtureAnonymizerTests: XCTestCase {
    // Synthetic ids (allow-listed in scripts/secret-scan-allow.txt).
    let idA = "00000000-0000-4000-8000-0000000000aa"   // account
    let idB = "00000000-0000-4000-8000-0000000000bb"   // personal org
    let idC = "00000000-0000-4000-8000-0000000000cc"
    let idD = "00000000-0000-4000-8000-0000000000dd"   // work org

    lazy var usage = try! JSONValue.parse("""
    {
      "five_hour": {"utilization": 44.0, "resets_at": "2026-10-26T03:59:59.512345+00:00"},
      "seven_day": {"utilization": 80.0, "resets_at": "2026-10-29T05:00:00.123456+00:00"},
      "seven_day_opus": null,
      "extra_usage": {"is_enabled": false, "monthly_limit": 20000, "used_credits": 333.0, "utilization": 1.6,
                      "currency": "AUD", "decimal_places": 2, "disabled_reason": "out_of_credits"},
      "limits": [
        {"kind": "session", "group": "session", "percent": 44, "severity": "normal",
         "resets_at": "2026-10-26T03:59:59.512345+00:00", "scope": null, "is_active": true},
        {"kind": "weekly_all", "group": "weekly", "percent": 80, "severity": "warning",
         "resets_at": "2026-10-29T05:00:00.123456+00:00", "scope": null, "is_active": false},
        {"kind": "weekly_scoped", "group": "weekly", "percent": 3, "severity": "normal",
         "resets_at": "2026-10-30T00:00:00Z", "scope": {"model": {"id": null, "display_name": "Fable"}}, "is_active": false}
      ],
      "note": "Secret Project Bluebird"
    }
    """)

    lazy var profile = try! JSONValue.parse("""
    {"account": {"uuid": "\(idA)", "email": "pat@example.org", "full_name": "Pat Sample", "display_name": "Pat",
                 "has_claude_max": true, "created_at": "2024-05-01T10:00:00Z"},
     "organization": {"uuid": "\(idB)", "name": "pat@example.org's Organization", "organization_type": "claude_max",
                      "billing_type": "stripe_subscription", "rate_limit_tier": "default_claude_max_20x", "seats": 9}}
    """)

    lazy var orgs = try! JSONValue.parse("""
    [{"uuid": "\(idB)", "name": "pat@example.org's Organization", "capabilities": ["chat", "claude_max"],
      "created_at": "2024-05-01T10:00:00.000Z", "settings": {"\(idC)": {"enabled": true}}},
     {"uuid": "\(idD)", "name": "Sample Widgets Pty Ltd", "capabilities": ["chat", "claude_team"],
      "billing_type": "Invoice Sample Widgets", "id": 98765, "joined": "2025-02-03"}]
    """)

    func run() -> (FixtureAnonymizer, [(JSONValue, JSONValue)]) {
        var a = FixtureAnonymizer(personalIDs: [idA.uppercased(), idB])
        let out = [(usage, a.anonymize(usage, document: .oauthUsage)),
                   (profile, a.anonymize(profile, document: .oauthProfile)),
                   (orgs, a.anonymize(orgs, document: .claudeAiOrganizations))]
        return (a, out)
    }

    /// Keys, nesting, array lengths, nulls, booleans and number spelling (int vs fraction) only.
    func shape(_ v: JSONValue) -> String {
        switch v {
        case .object(let ms): return "{" + ms.map { (FixtureAnonymizer.isUUID($0.key) ? "<uuid>" : $0.key) + ":" + shape($0.value) }.joined(separator: ",") + "}"
        case .array(let items): return "[" + items.map(shape).joined(separator: ",") + "]"
        case .string: return "s"
        case .number(let n): return n.contains(".") ? "f\(FixtureAnonymizer.fractionDigits(n))" : "i"
        case .bool(let b): return b ? "true" : "false"
        case .null: return "null"
        }
    }

    func testKeepsTheExactStructureAndValueTypes() {
        for (raw, out) in run().1 { XCTAssertEqual(shape(out), shape(raw)) }
    }

    func testNoInputStringOrNumberSurvivesExceptEnumsAndDecimalPlaces() {
        let (a, pairs) = run()
        let allowed: Set<String> = ["AUD", "out_of_credits", "session", "weekly", "weekly_all", "weekly_scoped",
                                    "normal", "warning", "Fable", "claude_max", "stripe_subscription",
                                    "default_claude_max_20x", "chat", "claude_team"]
        for (raw, out) in pairs {
            var rawStrings = Set<String>(), outStrings = Set<String>(), rawNumbers = Set<Double>(), outNumbers = Set<Double>()
            FixtureAnonymizer.collectLeaves(raw, strings: &rawStrings, numbers: &rawNumbers, mappedKeysOnly: true)
            FixtureAnonymizer.collectLeaves(out, strings: &outStrings, numbers: &outNumbers, mappedKeysOnly: false)
            XCTAssertEqual(rawStrings.intersection(outStrings).subtracting(allowed), [])
            XCTAssertEqual(rawNumbers.intersection(outNumbers), [])
            let text = out.serialized()
            for s in rawStrings.subtracting(allowed) where s.count >= 4 { XCTAssertFalse(text.contains(s), s) }
            XCTAssertTrue(a.leaks(raw: raw, anonymized: out).clean)
        }
        // decimal_places describes the currency and is the one number kept.
        XCTAssertEqual(pairs[0].1["extra_usage"]?["decimal_places"], .number("2"))
    }

    func testFakeValuesFollowTheWindowSoFallbackAndLimitsAgree() {
        let out = run().1[0].1
        XCTAssertEqual(out["five_hour"]?["utilization"], .number("33.0"))
        XCTAssertEqual(out["seven_day"]?["utilization"], .number("61.0"))
        guard case .array(let limits)? = out["limits"] else { return XCTFail("no limits") }
        XCTAssertEqual(limits.map { $0["percent"] }, [.number("33"), .number("61"), .number("7")])
        XCTAssertEqual(out["five_hour"]?["resets_at"], .string("2026-03-02T07:10:00.000000+00:00"))
        XCTAssertEqual(limits[0]["resets_at"], out["five_hour"]?["resets_at"])
        XCTAssertEqual(limits[1]["resets_at"], .string("2026-03-05T15:00:00.000000+00:00"))
        XCTAssertEqual(limits[2]["resets_at"], .string("2026-03-07T05:00:00Z"))
        XCTAssertEqual(limits[2]["scope"]?["model"]?["display_name"], .string("Fable"))
        XCTAssertEqual(out["extra_usage"]?["monthly_limit"], .number("10000"))
        XCTAssertEqual(out["extra_usage"]?["used_credits"], .number("1250.0"))
        XCTAssertEqual(out["extra_usage"]?["utilization"], .number("12.5"))
        XCTAssertEqual(out["note"], .string("example"))
    }

    func testIdsMapConsistentlyAndNamesFollowTheOwner() {
        let pairs = run().1
        let p = pairs[1].1, o = pairs[2].1
        XCTAssertEqual(p["account"]?["uuid"], .string("00000000-0000-4000-8000-000000000001"))
        XCTAssertEqual(p["organization"]?["uuid"], .string("00000000-0000-4000-8000-000000000002"))
        XCTAssertEqual(p["account"]?["email"], .string("personal@example.com"))
        XCTAssertEqual(p["account"]?["full_name"], .string("Example Personal"))
        XCTAssertEqual(p["organization"]?["name"], .string("Example Personal"))
        XCTAssertEqual(p["organization"]?["billing_type"], .string("stripe_subscription"))
        XCTAssertEqual(p["account"]?["created_at"], .string("2026-01-31T05:00:00Z"))
        guard case .array(let list)? = Optional(o) else { return XCTFail("not an array") }
        // The personal org keeps its id across documents and its personal name in the work document.
        XCTAssertEqual(list[0]["uuid"], p["organization"]?["uuid"])
        XCTAssertEqual(list[0]["name"], .string("Example Personal"))
        XCTAssertEqual(list[0]["settings"]?["00000000-0000-4000-8000-000000000003"], .object([JSONMember(key: "enabled", value: .bool(true))]))
        XCTAssertEqual(list[1]["uuid"], .string("00000000-0000-4000-8000-000000000004"))
        XCTAssertEqual(list[1]["name"], .string("Example Co"))
        XCTAssertEqual(list[1]["capabilities"], .array([.string("chat"), .string("claude_team")]))
        XCTAssertEqual(list[1]["billing_type"], .string("example"))   // not enum-shaped, so replaced
        XCTAssertEqual(list[1]["id"], .number("1"))
        XCTAssertEqual(list[1]["joined"], .string("2026-01-31"))
    }

    func testFakeNumbersNeverReuseAnInputNumber() {
        var a = FixtureAnonymizer()
        let raw = try! JSONValue.parse(#"{"five_hour": {"utilization": 20.0}, "other": 33.0, "more": 34}"#)
        let out = a.anonymize(raw, document: .oauthUsage)
        XCTAssertEqual(out["five_hour"]?["utilization"], .number("35.0"))
        XCTAssertTrue(a.leaks(raw: raw, anonymized: out).clean)
    }

    func testLeakCheckIgnoresKeyNamesButCatchesCopiedValues() {
        var a = FixtureAnonymizer()
        // "monthly" is replaced; that the key "monthly_limit" contains the word is not a leak.
        let raw = try! JSONValue.parse(#"{"period": "monthly", "monthly_limit": 5, "owner": "Pat Sample", "n": 41}"#)
        XCTAssertTrue(a.leaks(raw: raw, anonymized: a.anonymize(raw, document: .oauthUsage)).clean)
        // An output that copied input values (the bug the check guards against) is caught, by path.
        let copied = try! JSONValue.parse(#"{"period": "example", "monthly_limit": 1, "owner": "Pat Sample", "n": 41}"#)
        let check = a.leaks(raw: raw, anonymized: copied)
        XCTAssertEqual(check.strings, 1)
        XCTAssertEqual(check.numbers, 1)
        XCTAssertEqual(check.paths, ["$.owner"])
    }

    func testNumberingCanStartAfterIdsAlreadyInAFolder() {
        let existing = #"{"uuid": "00000000-0000-4000-8000-000000000007"}"#
        XCTAssertEqual(FixtureAnonymizer.highestFakeNumber(in: existing), 7)
        var a = FixtureAnonymizer(firstNumber: 8)
        let out = a.anonymize(try! JSONValue.parse(#"{"uuid": "\#(idA)"}"#), document: .oauthProfile)
        XCTAssertEqual(out["uuid"], .string("00000000-0000-4000-8000-000000000008"))
    }

    func testReportListsKeptValuesAndReplacedPathsOnly() {
        let a = run().0
        XCTAssertTrue(a.report.keptValues.contains("$.limits[].kind = weekly_scoped"))
        XCTAssertTrue(a.report.replacedPaths.contains("$.account.email"))
        XCTAssertFalse(a.report.keptValues.joined().contains("Pat"))
    }
}
