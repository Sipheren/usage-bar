import XCTest
@testable import UsageBarCore

/// Builds Cookies.binarycookies bytes by hand from the dtformats layout, so the parser is tested
/// against an independent construction, not a round-trip of its own encoder.
struct BinaryCookieBuilder {
    struct Cookie {
        var domain: String, name: String, path: String, value: String
        var expiry: Double          // seconds since Unix epoch
        var secure = false
        var httpOnly = false
    }

    static func le32(_ v: UInt32) -> [UInt8] { [0, 8, 16, 24].map { UInt8((v >> $0) & 0xFF) } }
    static func be32(_ v: UInt32) -> [UInt8] { [24, 16, 8, 0].map { UInt8((v >> $0) & 0xFF) } }
    static func le64(_ v: UInt64) -> [UInt8] { (0..<8).map { UInt8((v >> ($0 * 8)) & 0xFF) } }
    static func leDouble(_ d: Double) -> [UInt8] { le64(d.bitPattern) }

    static func record(_ c: Cookie) -> [UInt8] {
        // Header is 56 bytes; strings follow, each NUL-terminated, in url/name/path/value order.
        let headerLen = 56
        var strings: [UInt8] = []
        func put(_ s: String) -> UInt32 {
            let off = UInt32(headerLen + strings.count)
            strings.append(contentsOf: Array(s.utf8)); strings.append(0)
            return off
        }
        let urlOff = put(c.domain), nameOff = put(c.name), pathOff = put(c.path), valueOff = put(c.value)
        let size = UInt32(headerLen + strings.count)
        var flags: UInt32 = 0
        if c.secure { flags |= 0x1 }
        if c.httpOnly { flags |= 0x4 }
        let macExpiry = c.expiry - SafariBinaryCookies.macEpochOffset

        var rec: [UInt8] = []
        rec += le32(size)                 // 0: record size
        rec += le32(0)                    // 4: unknown
        rec += le32(flags)                // 8: flags
        rec += le32(0)                    // 12: unknown
        rec += le32(urlOff)               // 16
        rec += le32(nameOff)              // 20
        rec += le32(pathOff)              // 24
        rec += le32(valueOff)             // 28
        rec += le64(0)                    // 32: unknown
        rec += leDouble(macExpiry)        // 40: expiry
        rec += leDouble(macExpiry - 1000) // 48: creation
        rec += strings                    // 56: strings
        return rec
    }

    static func page(_ cookies: [Cookie]) -> [UInt8] {
        let records = cookies.map(record)
        let headerLen = 8 + records.count * 4 // page magic + count + offset array
        var offsets: [UInt8] = []
        var running = headerLen
        for r in records { offsets += le32(UInt32(running)); running += r.count }
        var page: [UInt8] = [0x00, 0x00, 0x01, 0x00]
        page += le32(UInt32(cookies.count))
        page += offsets
        for r in records { page += r }
        page += [0, 0, 0, 0]              // footer
        return page
    }

    static func file(pages: [[Cookie]]) -> Data {
        let built = pages.map(page)
        var out: [UInt8] = Array("cook".utf8)
        out += be32(UInt32(built.count))
        for p in built { out += be32(UInt32(p.count)) }
        for p in built { out += p }
        out += le64(0)                    // file footer / checksum
        return Data(out)
    }
}

final class SafariCookieParserTests: XCTestCase {
    let future = Date().addingTimeInterval(86_400 * 30).timeIntervalSince1970
    let past = Date().addingTimeInterval(-86_400).timeIntervalSince1970

    func sampleFile() -> Data {
        BinaryCookieBuilder.file(pages: [
            [BinaryCookieBuilder.Cookie(domain: ".claude.ai", name: "sessionKey", path: "/",
                                        value: "sk-sess-FAKE-VALUE", expiry: future, secure: true, httpOnly: true),
             BinaryCookieBuilder.Cookie(domain: "www.example.com", name: "other", path: "/", value: "x", expiry: future)],
            [BinaryCookieBuilder.Cookie(domain: "claude.ai", name: "lastActiveOrg", path: "/",
                                        value: "org-work-uuid", expiry: future),
             BinaryCookieBuilder.Cookie(domain: ".claude.ai", name: "expiredThing", path: "/",
                                        value: "y", expiry: past)],
        ])
    }

    func testParsesCookiesAcrossTwoPages() {
        let cookies = SafariBinaryCookies.parse(sampleFile())
        XCTAssertEqual(cookies.count, 4)
        let session = cookies.first { $0.name == "sessionKey" }
        XCTAssertEqual(session?.domain, ".claude.ai")
        XCTAssertEqual(session?.value, "sk-sess-FAKE-VALUE")
        XCTAssertEqual(session?.path, "/")
        XCTAssertTrue(session?.secure ?? false)
        XCTAssertTrue(session?.httpOnly ?? false)
    }

    func testExpiryUsesMacEpoch() {
        let session = SafariBinaryCookies.parse(sampleFile()).first { $0.name == "sessionKey" }
        XCTAssertEqual(session?.expiry?.timeIntervalSince1970 ?? 0, future, accuracy: 1)
    }

    func testRejectsNonCookFile() {
        XCTAssertEqual(SafariBinaryCookies.parse(Data("junkjunk".utf8)), [])
        XCTAssertEqual(SafariBinaryCookies.parse(Data()), [])
        // Truncated page-size table must not crash or over-read.
        XCTAssertEqual(SafariBinaryCookies.parse(Data(Array("cook".utf8) + [0, 0, 0, 9])), [])
    }

    func testReaderJarPrefersClaudeCookiesAndKeepsNames() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("safari-\(UUID().uuidString)")
        let cookieDir = dir.appendingPathComponent("Library/Containers/com.apple.Safari/Data/Library/Cookies")
        try FileManager.default.createDirectory(at: cookieDir, withIntermediateDirectories: true)
        try sampleFile().write(to: cookieDir.appendingPathComponent("Cookies.binarycookies"))
        defer { try? FileManager.default.removeItem(at: dir) }

        let reader = SafariCookieReader(home: dir.path)
        let jar = try reader.claudeCookieJar()
        XCTAssertEqual(jar["sessionKey"], "sk-sess-FAKE-VALUE")
        XCTAssertEqual(jar["lastActiveOrg"], "org-work-uuid")
        XCTAssertNil(jar["other"])   // non-claude.ai cookie excluded
    }

    func testReaderReportsNotFoundWhenNoFiles() {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("safari-empty-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        XCTAssertThrowsError(try SafariCookieReader(home: dir.path).claudeCookies()) { err in
            XCTAssertEqual(err as? SafariCookieError, .notFound)
        }
    }

    func testUnlistableContainerMeansFullDiskAccessNotNoSignIn() throws {
        // TCC denies listing the Safari container without Full Disk Access. That must be reported as an
        // access problem, never as "no sign-in found".
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("safari-tcc-\(UUID().uuidString)")
        let cookieDir = dir.appendingPathComponent("Library/Containers/com.apple.Safari/Data/Library/Cookies")
        try FileManager.default.createDirectory(at: cookieDir, withIntermediateDirectories: true)
        try sampleFile().write(to: cookieDir.appendingPathComponent("Cookies.binarycookies"))
        try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: cookieDir.path)
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: cookieDir.path)
            try? FileManager.default.removeItem(at: dir)
        }
        XCTAssertThrowsError(try SafariCookieReader(home: dir.path).claudeCookieJar()) { err in
            XCTAssertEqual(err as? SafariCookieError, .fullDiskAccess)
        }
    }

    func testReadableLegacyFileWithoutSessionButDeniedContainerIsFullDiskAccess() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("safari-mixed-\(UUID().uuidString)")
        let legacy = dir.appendingPathComponent("Library/Cookies")
        let container = dir.appendingPathComponent("Library/Containers/com.apple.Safari/Data/Library/Cookies")
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: container, withIntermediateDirectories: true)
        let noSession = BinaryCookieBuilder.file(pages: [[BinaryCookieBuilder.Cookie(
            domain: ".claude.ai", name: "anon", path: "/", value: "x", expiry: future)]])
        try noSession.write(to: legacy.appendingPathComponent("Cookies.binarycookies"))
        try sampleFile().write(to: container.appendingPathComponent("Cookies.binarycookies"))
        try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: container.path)
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: container.path)
            try? FileManager.default.removeItem(at: dir)
        }
        XCTAssertThrowsError(try SafariCookieReader(home: dir.path).claudeCookieJar()) { err in
            XCTAssertEqual(err as? SafariCookieError, .fullDiskAccess)
        }
    }

    func testReaderReportsFullDiskAccessOnPermissionError() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("safari-perm-\(UUID().uuidString)")
        let cookieDir = dir.appendingPathComponent("Library/Cookies")
        try FileManager.default.createDirectory(at: cookieDir, withIntermediateDirectories: true)
        let file = cookieDir.appendingPathComponent("Cookies.binarycookies")
        try sampleFile().write(to: file)
        // Make the file unreadable: produces NSFileReadNoPermissionError (257), the same code TCC returns.
        try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: file.path)
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: file.path)
            try? FileManager.default.removeItem(at: dir)
        }
        XCTAssertThrowsError(try SafariCookieReader(home: dir.path).claudeCookies()) { err in
            XCTAssertEqual(err as? SafariCookieError, .fullDiskAccess)
        }
    }
}

final class ClaudeAiCookieClientTests: XCTestCase {
    let orgs = [ClaudeAiOrg(uuid: "AAA-personal", name: "Personal"),
                ClaudeAiOrg(uuid: "BBB-work", name: "Work")]

    func testPickOrgPrefersLastActiveOrg() {
        let p = ClaudeAiCookieClient.pickOrg(orgs: orgs, lastActiveOrg: "bbb-work", personalOrg: "aaa-personal")
        XCTAssertEqual(p?.uuid, "BBB-work")
    }

    func testPickOrgFallsBackToNonPersonal() {
        let p = ClaudeAiCookieClient.pickOrg(orgs: orgs, lastActiveOrg: nil, personalOrg: "aaa-personal")
        XCTAssertEqual(p?.uuid, "BBB-work")
    }

    func testPickOrgFallsBackToFirstWhenNoHints() {
        let p = ClaudeAiCookieClient.pickOrg(orgs: orgs, lastActiveOrg: nil, personalOrg: nil)
        XCTAssertEqual(p?.uuid, "AAA-personal")
        XCTAssertNil(ClaudeAiCookieClient.pickOrg(orgs: [], lastActiveOrg: "x", personalOrg: nil))
    }

    func testPickOrgIgnoresLastActiveOrgNotInList() {
        let p = ClaudeAiCookieClient.pickOrg(orgs: orgs, lastActiveOrg: "ccc-missing", personalOrg: "aaa-personal")
        XCTAssertEqual(p?.uuid, "BBB-work")
    }

    func testParseOrganizations() {
        let json = #"[{"uuid":"a","name":"One"},{"id":"b"},{"name":"no uuid"}]"#
        let parsed = ClaudeAiCookieClient.parseOrganizations(Data(json.utf8))
        XCTAssertEqual(parsed, [ClaudeAiOrg(uuid: "a", name: "One"), ClaudeAiOrg(uuid: "b", name: nil)])
    }

    func testCloudflareDetection() throws {
        let url = URL(string: "https://claude.ai/")!
        let challengeHeader = HTTPURLResponse(url: url, statusCode: 403, httpVersion: nil,
                                              headerFields: ["cf-mitigated": "challenge"])!
        XCTAssertTrue(ClaudeAiCookieClient.isCloudflareChallenge(challengeHeader, body: Data()))
        let challengeBody = HTTPURLResponse(url: url, statusCode: 503, httpVersion: nil, headerFields: nil)!
        XCTAssertTrue(ClaudeAiCookieClient.isCloudflareChallenge(challengeBody,
                                                                body: Data("<title>Just a moment...</title>".utf8)))
        let ok = HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!
        XCTAssertFalse(ClaudeAiCookieClient.isCloudflareChallenge(ok, body: Data("{}".utf8)))
        let plain403 = HTTPURLResponse(url: url, statusCode: 403, httpVersion: nil, headerFields: nil)!
        XCTAssertFalse(ClaudeAiCookieClient.isCloudflareChallenge(plain403, body: Data(#"{"error":"x"}"#.utf8)))
    }

    func testPersonalOrgDetection() {
        XCTAssertTrue(ClaudeAiCookieClient.isPersonalOrg(ClaudeAiOrg(uuid: "AAA", name: "P"), personalOrg: "aaa"))
        XCTAssertFalse(ClaudeAiCookieClient.isPersonalOrg(ClaudeAiOrg(uuid: "BBB", name: "W"), personalOrg: "aaa"))
        XCTAssertFalse(ClaudeAiCookieClient.isPersonalOrg(ClaudeAiOrg(uuid: "AAA", name: "P"), personalOrg: nil))
    }

    func testScopeErrorDetection() {
        XCTAssertTrue(UsageClient.isScopeError(Data(#"{"error":{"message":"missing scope: usage"}}"#.utf8)))
        XCTAssertTrue(UsageClient.isScopeError(Data(#"{"type":"forbidden_scope"}"#.utf8)))
        XCTAssertFalse(UsageClient.isScopeError(Data(#"{"error":"not found"}"#.utf8)))
        XCTAssertFalse(UsageClient.isScopeError(Data()))
    }

    func testCookieHeaderJoinsPairs() {
        let header = ClaudeAiCookieClient.cookieHeader(from: ["sessionKey": "s", "lastActiveOrg": "o"])
        XCTAssertTrue(header.contains("sessionKey=s"))
        XCTAssertTrue(header.contains("lastActiveOrg=o"))
        XCTAssertTrue(header.contains("; "))
    }
}
