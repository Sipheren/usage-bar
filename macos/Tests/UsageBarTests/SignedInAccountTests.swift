import SQLite3
import XCTest
@testable import UsageBarCore

/// Answers each request from a table, so the fetch flows run without a network. A key is the URL, or the URL
/// and the bearer token for requests that carry one.
final class StubURLProtocol: URLProtocol {
    struct Reply { var status: Int; var body: String }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var replies: [String: Reply] = [:]
    nonisolated(unsafe) private static var log: [String] = []

    static func key(_ url: String, token: String) -> String { url + " " + token }

    static var requested: [String] { lock.lock(); defer { lock.unlock() }; return log }

    static func session(_ table: [String: Reply]) -> URLSession {
        lock.lock(); replies = table; log = []; lock.unlock()
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: config)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url?.absoluteString ?? ""
        let token = request.value(forHTTPHeaderField: "Authorization").map { String($0.dropFirst("Bearer ".count)) }
        StubURLProtocol.lock.lock()
        StubURLProtocol.log.append(url)
        let reply = token.flatMap { StubURLProtocol.replies[StubURLProtocol.key(url, token: $0)] }
            ?? StubURLProtocol.replies[url] ?? Reply(status: 404, body: "{}")
        StubURLProtocol.lock.unlock()
        let response = HTTPURLResponse(url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1",
                                       headerFields: ["Content-Type": "application/json"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(reply.body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

/// Each source shows the usage of whatever account it is signed in to. `~/.claude.json` names the Claude Code
/// account only; it never stops another source from showing numbers.
final class SignedInAccountTests: XCTestCase {
    let accountID = "00000000-0000-4000-8000-0000000000aa"
    let orgID = "00000000-0000-4000-8000-0000000000bb"
    let otherOrgID = "00000000-0000-4000-8000-0000000000cc"
    let usageJSON = #"{"five_hour": {"utilization": 20.0, "resets_at": "2026-03-02T07:10:00Z"}}"#
    var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("signed-in-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    // MARK: Helpers

    /// A `~/.claude.json` naming the same account and organization as the session under test.
    func claudeJSONNamingTheSession() throws -> String {
        let url = dir.appendingPathComponent("claude.json")
        let json = #"{"oauthAccount": {"accountUuid": "\#(accountID)", "organizationUuid": "\#(orgID)"}}"#
        try Data(json.utf8).write(to: url)
        return url.path
    }

    func claudeAi(orgs: [String]) -> [String: StubURLProtocol.Reply] {
        let list = orgs.map { #"{"uuid": "\#($0)", "name": "Org"}"# }.joined(separator: ", ")
        var table: [String: StubURLProtocol.Reply] = [ClaudeAiCookieClient.orgsURL.absoluteString: .init(status: 200, body: "[\(list)]")]
        for org in orgs { table["https://claude.ai/api/organizations/\(org)/usage"] = .init(status: 200, body: usageJSON) }
        return table
    }

    func fetcher(_ table: [String: StubURLProtocol.Reply], desktop: DesktopCredentialReader? = nil,
                 safari: SafariCookieReader? = nil) throws -> AccountFetcher {
        let session = StubURLProtocol.session(table)
        return AccountFetcher(desktop: desktop ?? DesktopCredentialReader(supportDir: dir.appendingPathComponent("none")),
                              safari: safari ?? SafariCookieReader(home: dir.path),
                              client: UsageClient(session: session),
                              cookieClient: ClaudeAiCookieClient(session: session),
                              claudeAccountInfoPath: try claudeJSONNamingTheSession())
    }

    /// A Safari cookie store under a temporary home with a claude.ai session.
    func safari(lastActiveOrg: String?) throws -> SafariCookieReader {
        let future = Date().addingTimeInterval(86_400).timeIntervalSince1970
        var cookies = [BinaryCookieBuilder.Cookie(domain: ".claude.ai", name: "sessionKey", path: "/",
                                                  value: "sk-FAKE", expiry: future)]
        if let lastActiveOrg {
            cookies.append(BinaryCookieBuilder.Cookie(domain: "claude.ai", name: "lastActiveOrg", path: "/",
                                                      value: lastActiveOrg, expiry: future))
        }
        let cookieDir = dir.appendingPathComponent("Library/Containers/com.apple.Safari/Data/Library/Cookies")
        try FileManager.default.createDirectory(at: cookieDir, withIntermediateDirectories: true)
        try BinaryCookieBuilder.file(pages: [cookies]).write(to: cookieDir.appendingPathComponent("Cookies.binarycookies"))
        return SafariCookieReader(home: dir.path)
    }

    /// A Claude app support folder signed in to `appAccount`, with a plain-text claude.ai session cookie (so no
    /// Keychain read is needed) and a token cache that is never decrypted.
    func claudeApp(appAccount: String) throws -> DesktopCredentialReader {
        let support = dir.appendingPathComponent("Claude", isDirectory: true)
        try FileManager.default.createDirectory(at: support, withIntermediateDirectories: true)
        let config = #"{"lastKnownAccountUuid": "\#(appAccount)", "oauth:tokenCacheV2": "djEwRkFLRQ=="}"#
        try Data(config.utf8).write(to: support.appendingPathComponent("config.json"))
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(support.appendingPathComponent("Cookies").path, &db), SQLITE_OK)
        defer { sqlite3_close(db) }
        let sql = "CREATE TABLE cookies (host_key TEXT, name TEXT, value TEXT, encrypted_value BLOB, expires_utc INTEGER);"
            + "INSERT INTO cookies VALUES ('.claude.ai', 'sessionKey', 'sk-FAKE', x'', 0);"
        XCTAssertEqual(sqlite3_exec(db, sql, nil, nil, nil), SQLITE_OK)
        return DesktopCredentialReader(supportDir: support)
    }

    let connected = DesktopContext(allowKeychain: true, cachedChoice: [:], knownEmail: nil)

    // MARK: Safari

    func testSafariIsShownWhenClaudeJSONNamesItsOrganization() async throws {
        let f = try fetcher(claudeAi(orgs: [orgID]), safari: try safari(lastActiveOrg: orgID))
        let report = await f.fetchSafari(now: Date())
        XCTAssertNil(report.issue)
        XCTAssertEqual(report.snapshot?.limits.count, 1)
    }

    func testSafariWithoutALastActiveOrgShowsTheFirstOrganization() async throws {
        let f = try fetcher(claudeAi(orgs: [orgID, otherOrgID]), safari: try safari(lastActiveOrg: nil))
        let report = await f.fetchSafari(now: Date())
        XCTAssertNil(report.issue)
        XCTAssertNotNil(report.snapshot)
        XCTAssertEqual(StubURLProtocol.requested.last, "https://claude.ai/api/organizations/\(orgID)/usage")
    }

    // MARK: Claude app

    func testClaudeAppIsShownWhenClaudeJSONNamesItsAccount() async throws {
        let f = try fetcher(claudeAi(orgs: [orgID]), desktop: try claudeApp(appAccount: accountID))
        let report = await f.fetchDesktop(connected, now: Date())
        XCTAssertNil(report.issue)
        XCTAssertEqual(report.snapshot?.limits.count, 1)
        XCTAssertEqual(report.route, "cookie")
    }

    func testClaudeAppCookieIsShownWhenClaudeJSONNamesItsOrganization() async throws {
        let f = try fetcher(claudeAi(orgs: [orgID]),
                            desktop: try claudeApp(appAccount: "00000000-0000-4000-8000-0000000000dd"))
        let report = await f.fetchDesktop(connected, now: Date())
        XCTAssertNil(report.issue)
        XCTAssertNotNil(report.snapshot)
    }

    func testClaudeAppStillWaitsForConnectWhenClaudeJSONNamesItsAccount() async throws {
        let f = try fetcher([:], desktop: try claudeApp(appAccount: accountID))
        let report = await f.fetchDesktop(DesktopContext(allowKeychain: false, cachedChoice: [:], knownEmail: nil),
                                          now: Date())
        XCTAssertEqual(report.issue, .notConnected)
        XCTAssertEqual(StubURLProtocol.requested, [])
    }

    // MARK: Claude app token choice

    func candidate(_ token: String) -> TokenCandidate {
        TokenCandidate(path: ["oauth:tokenCacheV2", token], accessToken: token,
                       expiresAt: Date().addingTimeInterval(3600), uuids: [])
    }

    func profile(account: String, org: String) -> StubURLProtocol.Reply {
        .init(status: 200, body: #"{"account": {"uuid": "\#(account)"}, "organization": {"uuid": "\#(org)"}}"#)
    }

    func testProbeTakesTheFirstValidTokenWhenNoneNamesTheAppAccount() async throws {
        let url = UsageClient.profileURL.absoluteString
        let f = try fetcher([
            StubURLProtocol.key(url, token: "tok-rejected"): .init(status: 401, body: "{}"),
            StubURLProtocol.key(url, token: "tok-first"): profile(account: accountID, org: orgID),
            StubURLProtocol.key(url, token: "tok-second"): profile(account: accountID, org: otherOrgID),
        ])
        let (chosen, matched) = await f.probe([candidate("tok-rejected"), candidate("tok-first"), candidate("tok-second")],
                                              accountUuid: nil)
        XCTAssertEqual(chosen?.accessToken, "tok-first")
        XCTAssertFalse(matched)
    }

    func testProbePrefersTheTokenForTheAppsOwnAccount() async throws {
        let url = UsageClient.profileURL.absoluteString
        let appAccount = "00000000-0000-4000-8000-0000000000dd"
        let f = try fetcher([
            StubURLProtocol.key(url, token: "tok-other"): profile(account: accountID, org: otherOrgID),
            StubURLProtocol.key(url, token: "tok-app"): profile(account: appAccount, org: orgID),
        ])
        let (chosen, matched) = await f.probe([candidate("tok-other"), candidate("tok-app")],
                                              accountUuid: appAccount)
        XCTAssertEqual(chosen?.accessToken, "tok-app")
        XCTAssertTrue(matched)
    }
}
