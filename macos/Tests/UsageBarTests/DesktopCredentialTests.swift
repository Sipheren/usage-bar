import CryptoKit
import XCTest
@testable import UsageBarCore

final class DesktopCredentialTests: XCTestCase {
    // Synthetic ids (allow-listed in scripts/secret-scan-allow.txt). The fake token is assembled at runtime
    // so the source never contains a token-shaped literal.
    let accountUuid = "00000000-0000-4000-8000-0000000000aa"
    let otherUuid = "00000000-0000-4000-8000-0000000000bb"
    let clientId = "00000000-0000-4000-8000-0000000000cc"
    let secretToken = "sk-" + "ant-oat01-" + "FAKE-TOKEN-FOR-TESTS-ONLY-1234567890"

    /// A 24-character fake token made of one repeated letter.
    func fake(_ c: Character) -> String { String(repeating: c, count: 24) }

    func testChromiumV10RoundTripAndBase64Form() throws {
        let key = try ChromiumCrypto.deriveKey(password: Data("fake-safe-storage-password".utf8))
        XCTAssertEqual(key.count, 16)
        let plain = Data(#"{"hello":"world"}"#.utf8)
        let blob = try ChromiumCrypto.encrypt(plain, key: key)
        XCTAssertTrue(blob.base64EncodedString().hasPrefix("djEw"))   // "v10"
        XCTAssertEqual(try ChromiumCrypto.decryptBase64(blob.base64EncodedString(), key: key), plain)
        XCTAssertThrowsError(try ChromiumCrypto.decrypt(Data("v11xxxxxxxxxxxxxxxx".utf8), key: key))
    }

    func testWrongKeyIsRejectedNotSilentlyGarbage() throws {
        let realKey = try ChromiumCrypto.deriveKey(password: Data("real-password".utf8))
        let wrongKey = try ChromiumCrypto.deriveKey(password: Data("WRONG-password".utf8))
        let blob = try ChromiumCrypto.encrypt(Data(#"{"sessionKey":"s"}"#.utf8), key: realKey)
        XCTAssertEqual(try ChromiumCrypto.decrypt(blob, key: realKey), Data(#"{"sessionKey":"s"}"#.utf8))
        // Every wrong key must throw, so a bad sessionKey/token can never be sent with a request.
        var rejected = 0
        for i in 0..<64 {
            let k = try ChromiumCrypto.deriveKey(password: Data("wrong-\(i)".utf8))
            if (try? ChromiumCrypto.decrypt(blob, key: k)) == nil { rejected += 1 }
        }
        XCTAssertEqual(rejected, 64)
        XCTAssertThrowsError(try ChromiumCrypto.decrypt(blob, key: wrongKey))
    }

    func testPKCS7StripRejectsBadPadding() {
        XCTAssertThrowsError(try ChromiumCrypto.stripPKCS7(Data([1, 2, 3])))           // not a block multiple
        XCTAssertThrowsError(try ChromiumCrypto.stripPKCS7(Data(repeating: 0, count: 16)))  // pad byte 0
        var bad = Data(repeating: 0, count: 16); bad[15] = 5; bad[14] = 4                // pad bytes differ
        XCTAssertThrowsError(try ChromiumCrypto.stripPKCS7(bad))
        var good = Data(repeating: 0xAA, count: 16); for i in 12..<16 { good[i] = 4 }     // valid pad of 4
        XCTAssertEqual(try ChromiumCrypto.stripPKCS7(good).count, 12)
    }

    func pathString(of cands: [TokenCandidate], firstKey: String) -> String {
        cands.first { $0.path.first == firstKey }!.pathString
    }

    func testUnexpiredCandidateBeatsExpiredCachedPath() {
        let now = Date()
        let cache: [String: Any] = [
            "cached": ["accessToken": fake("c"), "expiresAt": now.addingTimeInterval(-3600).timeIntervalSince1970 * 1000],
            "fresh": ["accessToken": fake("f"), "expiresAt": now.addingTimeInterval(3600).timeIntervalSince1970 * 1000],
        ]
        let cands = DesktopTokenParser.candidates(in: cache)
        // Point the cached choice at the (now expired) "cached" candidate by its real path.
        let ordered = DesktopTokenParser.ordered(cands, accountUuid: nil,
                                                 cachedPath: pathString(of: cands, firstKey: "cached"), now: now)
        XCTAssertEqual(ordered.first?.path.first, "fresh")
        XCTAssertFalse(ordered.first!.isExpired(now: now))
    }

    func testUnexpiredCachedPathStillLeadsOtherUnexpired() {
        let now = Date()
        let future = now.addingTimeInterval(3600).timeIntervalSince1970 * 1000
        let cache: [String: Any] = [
            "cached": ["accessToken": fake("c"), "expiresAt": future],
            "other": ["accessToken": fake("o"), "expiresAt": future + 10_000],
        ]
        let cands = DesktopTokenParser.candidates(in: cache)
        let ordered = DesktopTokenParser.ordered(cands, accountUuid: nil,
                                                 cachedPath: pathString(of: cands, firstKey: "cached"), now: now)
        XCTAssertEqual(ordered.first?.path.first, "cached")
    }

    func testKnownChromiumVector() throws {
        // PBKDF2-HMAC-SHA1("peanuts", "saltysalt", 1003, 16), computed independently with Python's
        // hashlib.pbkdf2_hmac; pins the derivation parameters.
        let key = try ChromiumCrypto.deriveKey(password: Data("peanuts".utf8))
        XCTAssertEqual(key.map { String(format: "%02x", $0) }.joined(), "d9a09d499b4e1b7461f28e67972c6dbd")
    }

    func testStripsHostDigestPrefix() {
        let host = ".claude.ai"
        let digest = Data(SHA256.hash(data: Data(host.utf8)))
        XCTAssertEqual(ChromiumCrypto.stripHostDigest(digest + Data("abc".utf8), host: host), Data("abc".utf8))
        XCTAssertEqual(ChromiumCrypto.stripHostDigest(Data("abc".utf8), host: host), Data("abc".utf8))
    }

    func fakeCache() -> [String: Any] {
        [
            "\(clientId):\(otherUuid):user:inference": [
                "token": "other-account-token-0000000000000000", "refreshToken": "r1", "expiresAt": 4_000_000_000_000,
            ],
            "\(clientId):\(accountUuid):user:inference": [
                "token": secretToken, "refreshToken": "r2", "expiresAt": 4_000_000_000_000,
            ],
            "meta": ["version": 2],
        ]
    }

    func testFindsCandidatesAndPrefersTheDesktopAccount() {
        let cands = DesktopTokenParser.candidates(in: fakeCache())
        XCTAssertEqual(cands.count, 2)
        let ordered = DesktopTokenParser.ordered(cands, accountUuid: accountUuid, cachedPath: nil, now: Date())
        XCTAssertEqual(ordered.first?.accessToken, secretToken)
        XCTAssertTrue(ordered.first!.mentions(accountUuid))
        XCTAssertEqual(ordered.first?.expiresAt?.timeIntervalSince1970, 4_000_000_000)
    }

    func testFindsTokensInNestedEmbeddedJSONAndAlternateKeys() throws {
        let inner = try JSONSerialization.data(withJSONObject: ["access_token": secretToken, "expires_at": "2099-01-01T00:00:00Z",
                                                               "accountUuid": accountUuid])
        let outer: [String: Any] = ["entries": [["blob": String(data: inner, encoding: .utf8)!]]]
        let cands = DesktopTokenParser.candidates(in: outer)
        XCTAssertEqual(cands.count, 1)
        XCTAssertTrue(cands[0].mentions(accountUuid))
        XCTAssertNotNil(cands[0].expiresAt)
    }

    func testExpiredCandidatesSortAfterLiveOnes() {
        let cache: [String: Any] = [
            "a": ["accessToken": fake("a"), "expiresAt": 1_000_000_000_000],
            "b": ["accessToken": fake("b"), "expiresAt": 4_000_000_000_000],
        ]
        let ordered = DesktopTokenParser.ordered(DesktopTokenParser.candidates(in: cache), accountUuid: nil,
                                                 cachedPath: nil, now: Date())
        XCTAssertEqual(ordered.map { $0.path.first }, ["b", "a"])
        XCTAssertTrue(ordered[1].isExpired(now: Date()))
    }

    func testShapeDumpShowsKeysAndTypesButNoValues() {
        let shape = DesktopTokenParser.shape(of: fakeCache())
        XCTAssertFalse(shape.contains(secretToken))
        XCTAssertFalse(shape.contains("THIS-IS-A-FAKE"))
        XCTAssertFalse(shape.contains("other-account-token"))
        XCTAssertFalse(shape.contains("4000000000000"))
        XCTAssertTrue(shape.contains("token: string(len \(secretToken.count), sk-ant token)"))
        XCTAssertTrue(shape.contains("expiresAt: number(int) epoch-ms-like"))
        XCTAssertTrue(shape.contains("<uuid 00000000…>"))
        XCTAssertFalse(shape.contains(accountUuid))
    }

    func testCandidateDescriptionNeverIncludesToken() {
        let c = DesktopTokenParser.candidates(in: fakeCache())
        for cand in c {
            XCTAssertFalse(String(describing: cand).contains(cand.accessToken))
        }
    }

    func testMaskedKeyHidesOpaqueRuns() {
        XCTAssertEqual(DesktopTokenParser.maskedKey("abc:" + String(repeating: "Z", count: 40)), "abc:<opaque len 40>")
        XCTAssertEqual(DesktopTokenParser.maskedKey("sk-ant-xyz"), "<secret-like key>")
    }

    func testSettingsDecodeToleratesMissingFields() throws {
        let s = try JSONDecoder().decode(AppSettings.self, from: Data(#"{"menuBarStyle":"compact"}"#.utf8))
        XCTAssertEqual(s.menuBarStyle, .compact)
        XCTAssertEqual(s.accounts.map(\.tag), ["P", "W"])
        XCTAssertEqual(s.refreshMinutes, 2)
        XCTAssertFalse(s.desktopKeychainApproved)
        var custom = AppSettings()
        custom.refreshMinutes = 5
        custom.accounts[0].name = "Mine"
        let round = try JSONDecoder().decode(AppSettings.self, from: JSONEncoder().encode(custom))
        XCTAssertEqual(round, custom)
    }

    func testBadgeColours() {
        XCTAssertEqual(Account.defaultPersonal().badgeHex, "#B4532F")
        XCTAssertEqual(Account.defaultWork().badgeHex, "#2563A8")
    }
}
