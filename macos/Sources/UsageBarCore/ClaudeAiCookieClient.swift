import Foundation

public struct ClaudeAiOrg: Equatable, Sendable {
    public var uuid: String
    public var name: String?
}

public enum CookieUsageOutcome: Sendable {
    case ok(UsageSnapshot, org: ClaudeAiOrg)
    case noSession
    case cloudflare
    case unauthorized(Int)
    case scopeError
    /// HTTP 429 from either request, with its Retry-After (nil when missing).
    case rateLimited(retryAfter: TimeInterval?)
    case http(Int)
    case undecodable
    case noOrganizations
    case network(String)
}

/// Fetches claude.ai usage with a browser session cookie (Safari's, or the Claude app's).
/// Read-only: never writes cookies or tokens.
public struct ClaudeAiCookieClient: Sendable {
    public static let orgsURL = URL(string: "https://claude.ai/api/organizations")!
    // A plain browser-like User-Agent; claude.ai rejects obviously non-browser agents on this surface.
    public static let browserUA =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"

    let session: URLSession

    public init() {
        let config = URLSessionConfiguration.ephemeral
        config.urlCache = nil
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.timeoutIntervalForRequest = 20
        self.init(session: URLSession(configuration: config))
    }

    init(session: URLSession) {
        self.session = session
    }

    /// Picks the organization to show: the `lastActiveOrg` cookie if it's one of the orgs, otherwise the first.
    public static func pickOrg(orgs: [ClaudeAiOrg], lastActiveOrg: String?) -> ClaudeAiOrg? {
        if let last = lastActiveOrg?.lowercased(), let m = orgs.first(where: { $0.uuid.lowercased() == last }) { return m }
        return orgs.first
    }

    public static func parseOrganizations(_ data: Data) -> [ClaudeAiOrg] {
        guard let arr = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
        return arr.compactMap { obj in
            guard let uuid = obj["uuid"] as? String ?? obj["id"] as? String else { return nil }
            return ClaudeAiOrg(uuid: uuid, name: obj["name"] as? String)
        }
    }

    /// True when the response is a Cloudflare interstitial rather than the API.
    public static func isCloudflareChallenge(_ http: HTTPURLResponse, body: Data) -> Bool {
        if let m = http.value(forHTTPHeaderField: "cf-mitigated"), m.lowercased().contains("challenge") { return true }
        if http.statusCode == 403 || http.statusCode == 503 || http.statusCode == 429 {
            if let s = String(data: body.prefix(2048), encoding: .utf8),
               s.contains("Just a moment") || s.contains("cf-browser-verification") || s.contains("challenge-platform") {
                return true
            }
        }
        return false
    }

    /// A Cloudflare challenge, then a 429, ends the flow on either request. Nil for any other response.
    static func blockedOutcome(_ http: HTTPURLResponse, body: Data, now: Date) -> CookieUsageOutcome? {
        if isCloudflareChallenge(http, body: body) { return .cloudflare }
        guard http.statusCode == 429 else { return nil }
        return .rateLimited(retryAfter: UsageClient.retryAfter(http.value(forHTTPHeaderField: "Retry-After"), now: now))
    }

    private func request(_ url: URL, cookieHeader: String) -> URLRequest {
        var r = URLRequest(url: url)
        r.httpMethod = "GET"
        r.setValue(cookieHeader, forHTTPHeaderField: "Cookie")
        r.setValue("application/json", forHTTPHeaderField: "Accept")
        r.setValue(ClaudeAiCookieClient.browserUA, forHTTPHeaderField: "User-Agent")
        r.setValue("https://claude.ai/", forHTTPHeaderField: "Referer")
        return r
    }

    /// One GET with the session cookie, nothing decoded. For the fixture export only.
    public func rawGET(_ url: URL, cookies jar: [String: String]) async throws -> RawResponse {
        try await RawResponse.get(request(url, cookieHeader: ClaudeAiCookieClient.cookieHeader(from: jar)), session: session)
    }

    static func cookieHeader(from jar: [String: String]) -> String {
        jar.map { "\($0.key)=\($0.value)" }.joined(separator: "; ")
    }

    /// Full flow: organizations → pick → usage. `jar` must include `sessionKey`.
    public func fetchUsage(cookies jar: [String: String]) async -> (CookieUsageOutcome, Int) {
        guard jar["sessionKey"] != nil else { return (.noSession, 0) }
        let header = ClaudeAiCookieClient.cookieHeader(from: jar)
        do {
            let (orgData, orgResp) = try await session.data(for: request(ClaudeAiCookieClient.orgsURL, cookieHeader: header))
            guard let orgHTTP = orgResp as? HTTPURLResponse else { return (.http(0), 0) }
            if let blocked = ClaudeAiCookieClient.blockedOutcome(orgHTTP, body: orgData, now: Date()) {
                return (blocked, orgHTTP.statusCode)
            }
            if orgHTTP.statusCode == 401 || orgHTTP.statusCode == 403 { return (.unauthorized(orgHTTP.statusCode), orgHTTP.statusCode) }
            guard orgHTTP.statusCode == 200 else { return (.http(orgHTTP.statusCode), orgHTTP.statusCode) }

            let orgs = ClaudeAiCookieClient.parseOrganizations(orgData)
            guard let org = ClaudeAiCookieClient.pickOrg(orgs: orgs, lastActiveOrg: jar["lastActiveOrg"]) else {
                return (.noOrganizations, orgHTTP.statusCode)
            }
            guard let usageURL = URL(string: "https://claude.ai/api/organizations/\(org.uuid)/usage") else {
                return (.noOrganizations, orgHTTP.statusCode)
            }
            let (data, resp) = try await session.data(for: request(usageURL, cookieHeader: header))
            guard let http = resp as? HTTPURLResponse else { return (.http(0), 0) }
            if let blocked = ClaudeAiCookieClient.blockedOutcome(http, body: data, now: Date()) {
                return (blocked, http.statusCode)
            }
            switch http.statusCode {
            case 200:
                guard let snap = try? UsageDecoder.decode(data, fetchedAt: Date()) else { return (.undecodable, 200) }
                return (.ok(snap, org: org), 200)
            case 401:
                return (.unauthorized(401), 401)
            case 403:
                return (UsageClient.isScopeError(data) ? .scopeError : .unauthorized(403), 403)
            default:
                return (.http(http.statusCode), http.statusCode)
            }
        } catch {
            return (.network(AccountFetcher.shortNetworkMessage(error)), 0)
        }
    }
}
