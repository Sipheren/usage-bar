import Foundation

public enum FetchOutcome: Sendable {
    case ok(UsageSnapshot)
    case unauthorized(Int)
    /// 403 whose body says the token lacks the scope to read usage.
    case scopeError
    case rateLimited(retryAfter: TimeInterval?)
    case http(Int)
    case undecodable
}

/// Talks to the usage and profile endpoints. Tokens only ever live in request headers.
public struct UsageClient: Sendable {
    public static let usageURL = URL(string: "https://api.anthropic.com/api/oauth/usage")!
    public static let profileURL = URL(string: "https://api.anthropic.com/api/oauth/profile")!

    let session: URLSession

    public init() {
        let config = URLSessionConfiguration.ephemeral
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.httpCookieStorage = nil
        config.httpShouldSetCookies = false
        config.timeoutIntervalForRequest = 20
        session = URLSession(configuration: config)
    }

    func oauthRequest(_ url: URL, token: String) -> URLRequest {
        var r = URLRequest(url: url)
        r.httpMethod = "GET"
        r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        r.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        r.setValue("application/json", forHTTPHeaderField: "Accept")
        r.setValue(ClaudeCLIVersion.userAgent, forHTTPHeaderField: "User-Agent")
        return r
    }

    /// Returns the outcome plus the raw HTTP status (0 if none).
    public func fetchUsage(token: String) async throws -> (FetchOutcome, Int) {
        try await perform(oauthRequest(UsageClient.usageURL, token: token))
    }

    public func fetchProfile(token: String) async throws -> (Profile?, Int) {
        let (data, response) = try await session.data(for: oauthRequest(UsageClient.profileURL, token: token))
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else { return (nil, status) }
        return (Profile.decode(data), status)
    }

    private func perform(_ request: URLRequest) async throws -> (FetchOutcome, Int) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { return (.http(0), 0) }
        switch http.statusCode {
        case 200:
            guard let snap = try? UsageDecoder.decode(data, fetchedAt: Date()) else { return (.undecodable, 200) }
            return (.ok(snap), 200)
        case 401:
            return (.unauthorized(http.statusCode), http.statusCode)
        case 403:
            return (UsageClient.isScopeError(data) ? .scopeError : .unauthorized(403), 403)
        case 429:
            let ra = UsageClient.retryAfter(http.value(forHTTPHeaderField: "Retry-After"), now: Date())
            return (.rateLimited(retryAfter: ra), 429)
        default:
            return (.http(http.statusCode), http.statusCode)
        }
    }

    /// True when a 403 body indicates the token can't read usage because of its OAuth scope.
    public static func isScopeError(_ data: Data) -> Bool {
        guard let s = String(data: data.prefix(2048), encoding: .utf8)?.lowercased() else { return false }
        return s.contains("scope") || s.contains("insufficient_permission") || s.contains("forbidden_scope")
    }

    /// Retry-After as delta-seconds or an HTTP date.
    public static func retryAfter(_ value: String?, now: Date) -> TimeInterval? {
        guard let v = value?.trimmingCharacters(in: .whitespaces), !v.isEmpty else { return nil }
        if let secs = Double(v) { return max(0, secs) }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "GMT")
        for format in ["EEE, dd MMM yyyy HH:mm:ss zzz", "EEEE, dd-MMM-yy HH:mm:ss zzz", "EEE MMM d HH:mm:ss yyyy"] {
            f.dateFormat = format
            if let d = f.date(from: v) { return max(0, d.timeIntervalSince(now)) }
        }
        return nil
    }
}

// MARK: - Exchange rate

/// US-dollar exchange rates from open.er-api.com (units of each currency per US$1).
public struct ExchangeRates: Codable, Equatable, Sendable {
    public var rates: [String: Double]
    public var asOf: Date
    public var fetchedAt: Date

    public init(rates: [String: Double], asOf: Date, fetchedAt: Date) {
        self.rates = rates
        self.asOf = asOf
        self.fetchedAt = fetchedAt
    }

    /// Used until the first fetch: AUD only (the original default currency), rate of 2026-10-25.
    public static let fallback = ExchangeRates(rates: ["USD": 1, "AUD": 1.423],
                                               asOf: ISODate.parse("2026-10-25T00:00:00Z")!, fetchedAt: .distantPast)

    public var isFallback: Bool { fetchedAt == .distantPast }

    public func needsRefresh(now: Date) -> Bool { now.timeIntervalSince(fetchedAt) > 24 * 3600 }

    /// One shared session, reused across daily refreshes.
    private static let session: URLSession = {
        let c = URLSessionConfiguration.ephemeral
        c.urlCache = nil
        c.timeoutIntervalForRequest = 20
        return URLSession(configuration: c)
    }()

    public static func fetch() async throws -> ExchangeRates {
        let url = URL(string: "https://open.er-api.com/v6/latest/USD")!
        var r = URLRequest(url: url)
        r.timeoutInterval = 20
        let (data, response) = try await session.data(for: r)
        guard (response as? HTTPURLResponse)?.statusCode == 200,
              let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let raw = obj["rates"] as? [String: Any] else { throw URLError(.cannotParseResponse) }
        var rates: [String: Double] = [:]
        for (code, value) in raw {
            if let n = value as? NSNumber, n.doubleValue > 0 { rates[code.uppercased()] = n.doubleValue }
        }
        guard !rates.isEmpty else { throw URLError(.cannotParseResponse) }
        rates["USD"] = 1
        let asOf = (obj["time_last_update_unix"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue) } ?? Date()
        return ExchangeRates(rates: rates, asOf: asOf, fetchedAt: Date())
    }
}
