import Foundation

public enum AccountIssue: Equatable, Sendable {
    case expiredCLI
    case expiredDesktop
    case keychainDenied
    /// Work account not connected yet: reading Claude Safe Storage needs a one-time Keychain approval.
    case notConnected
    /// Reading Safari's cookie store needs Full Disk Access.
    case fullDiskAccess
    /// claude.ai returned a Cloudflare challenge instead of the API.
    case cloudflareChallenge
    /// The sign-in works but lacks the OAuth scope to read usage.
    case scope
    /// This session is signed in to the user's Personal account (shown under Work); no numbers.
    case signedInToPersonal(String)
    case notSignedIn(String)
    case network(String)
    case rejected(String)
    case http(Int)
    case rateLimited(until: Date)
    case unsupported(String)

    public var message: String { message(timeZone: .current) }

    public func message(timeZone: TimeZone) -> String {
        switch self {
        case .expiredCLI: return "Claude Code login expired — run `claude` once"
        case .expiredDesktop: return "Open the Claude app to refresh its sign-in"
        case .keychainDenied: return "Allow Usage Bar to read Claude Safe Storage"
        case .notConnected: return "Allow Usage Bar to read Claude Safe Storage"
        case .fullDiskAccess: return "Give Usage Bar Full Disk Access"
        case .cloudflareChallenge: return "Blocked by a Cloudflare check — open claude.ai in Safari"
        case .scope: return "This sign-in can't read usage (scope)"
        case .signedInToPersonal(let who): return "\(who) is signed in to your Personal account"
        case .notSignedIn(let s): return s
        case .network(let s): return "Network error: \(s)"
        case .rejected(let s): return s
        case .http(let code): return "Usage request failed (HTTP \(code))"
        case .rateLimited(let until):
            return "Rate limited — retrying after \(Formatters.clockTime(until, timeZone: timeZone))"
        case .unsupported(let s): return s
        }
    }

    /// Issues that mean the shown values are old.
    public var greysValues: Bool {
        switch self {
        case .notConnected: return false
        default: return true
        }
    }

    /// Case name only (no associated text), safe to log.
    public var logName: String {
        switch self {
        case .expiredCLI: return "expiredCLI"
        case .expiredDesktop: return "expiredDesktop"
        case .keychainDenied: return "keychainDenied"
        case .notConnected: return "notConnected"
        case .fullDiskAccess: return "fullDiskAccess"
        case .cloudflareChallenge: return "cloudflareChallenge"
        case .scope: return "scope"
        case .signedInToPersonal: return "signedInToPersonal"
        case .notSignedIn: return "notSignedIn"
        case .network: return "network"
        case .rejected: return "rejected"
        case .http(let code): return "http\(code)"
        case .rateLimited: return "rateLimited"
        case .unsupported: return "unsupported"
        }
    }

    /// Whether the dropdown/Settings should offer a Full Disk Access button.
    public var needsFullDiskAccess: Bool { self == .fullDiskAccess }
}

public struct FetchReport: Sendable {
    public var snapshot: UsageSnapshot?
    public var issue: AccountIssue?
    /// Last HTTP status from the usage endpoint, if a request was made.
    public var httpStatus: Int?
    public var retryAfter: TimeInterval?
    /// The random part of a 429 wait (FetchPolicy.rateLimitJitterRange), for the log.
    public var rateLimitJitter: TimeInterval?
    public var email: String?
    /// Desktop: key path of the chosen token-cache entry (no secrets), to cache per account uuid.
    public var desktopChoice: (accountUuid: String, path: String)?
    /// "token" or "cookie" for the desktop account.
    public var route: String?
    /// True once the Safe Storage key has been read in this process.
    public var keychainOK = false

    public init() {}
}

public struct DesktopContext: Sendable {
    public var allowKeychain: Bool
    public var cachedChoice: [String: String]
    public var knownEmail: String?

    public init(allowKeychain: Bool, cachedChoice: [String: String], knownEmail: String?) {
        self.allowKeychain = allowKeychain
        self.cachedChoice = cachedChoice
        self.knownEmail = knownEmail
    }
}

/// One fetch per account. Never refreshes or writes either app's credentials.
public final class AccountFetcher: @unchecked Sendable {
    public let client = UsageClient()
    public let cookieClient = ClaudeAiCookieClient()
    public let desktop: DesktopCredentialReader
    public let safari: SafariCookieReader

    public init(desktop: DesktopCredentialReader = DesktopCredentialReader(),
                safari: SafariCookieReader = SafariCookieReader()) {
        self.desktop = desktop
        self.safari = safari
    }

    public func fetch(_ account: Account, desktopContext: DesktopContext, now: Date = Date()) async -> FetchReport {
        let report: FetchReport
        switch account.source {
        case .claudeCode(let dir): report = await fetchClaudeCode(configDir: dir, now: now)
        case .claudeDesktop: report = await fetchDesktop(desktopContext, now: now)
        case .safariSession: report = await fetchSafari(now: now)
        }
        let kind = account.source.kind.rawValue
        let status = report.httpStatus ?? 0
        if report.snapshot != nil {
            Log.fetch.info("\(kind, privacy: .public): ok, HTTP \(status, privacy: .public), \(report.snapshot?.limits.count ?? 0, privacy: .public) limits, route \(report.route ?? "-", privacy: .public)")
        } else {
            Log.fetch.notice("\(kind, privacy: .public): \(report.issue?.logName ?? "noData", privacy: .public), HTTP \(status, privacy: .public)")
        }
        return report
    }

    // MARK: Safari session

    public func fetchSafari(now: Date) async -> FetchReport {
        var report = FetchReport()
        report.route = "safari"
        let jar: [String: String]
        do {
            jar = try await Task.detached(priority: .utility) { [safari] in try safari.claudeCookieJar() }.value
        } catch SafariCookieError.fullDiskAccess {
            report.issue = .fullDiskAccess
            return report
        } catch SafariCookieError.notFound {
            report.issue = .notSignedIn("No claude.ai sign-in found in Safari")
            return report
        } catch {
            report.issue = .notSignedIn("No claude.ai sign-in found in Safari")
            return report
        }
        Log.auth.info("safari: \(jar.count, privacy: .public) claude.ai cookies, session \(jar["sessionKey"] != nil ? "present" : "absent", privacy: .public)")
        guard jar["sessionKey"] != nil else {
            report.issue = .notSignedIn("No claude.ai sign-in found in Safari")
            return report
        }
        let personalOrg = ClaudeCodeAccountInfo.read()?.organizationUuid
        let (outcome, status) = await cookieClient.fetchUsage(cookies: jar, personalOrg: personalOrg)
        report.httpStatus = status
        AccountFetcher.applyCookieOutcome(outcome, report: &report,
                                          rejected: .notSignedIn("Safari sign-in rejected — open claude.ai in Safari"),
                                          now: Date())
        return report
    }

    static func applyCookieOutcome(_ outcome: CookieUsageOutcome, report: inout FetchReport, rejected: AccountIssue,
                                   now: Date, jitter: TimeInterval? = nil) {
        switch outcome {
        case .ok(let snap, let org):
            report.snapshot = snap
            report.issue = nil
            if report.email == nil { report.email = org.name }
        case .noSession:
            report.issue = .notSignedIn("No claude.ai sign-in found in Safari")
        case .cloudflare:
            report.issue = .cloudflareChallenge
        case .unauthorized:
            report.issue = rejected
        case .scopeError:
            report.issue = .scope
        case .signedInToPersonal:
            report.issue = .signedInToPersonal(report.route == "safari" ? "Safari" : "This session")
        case .noOrganizations:
            report.issue = .rejected("No organizations on this claude.ai session")
        case .rateLimited(let ra):
            rateLimited(ra, report: &report, now: now, jitter: jitter)
        case .http(let code):
            report.issue = .http(code)
        case .undecodable:
            report.issue = .rejected("Unexpected usage response")
        case .network(let s):
            report.issue = .network(s)
        }
    }

    // MARK: Claude Code

    public func fetchClaudeCode(configDir: String, now: Date) async -> FetchReport {
        var report = FetchReport()
        report.email = ClaudeCodeAccountInfo.read()?.email
        let token: CLIToken
        do {
            token = try await ClaudeCodeCredentials.read(configDir: configDir)
        } catch CredentialError.notFound {
            report.issue = .notSignedIn("Not signed in to Claude Code — run `claude` once")
            return report
        } catch CredentialError.unsupported(let s) {
            report.issue = .unsupported(s)
            return report
        } catch CredentialError.timedOut {
            report.issue = .rejected("Keychain read timed out — try again")
            return report
        } catch {
            report.issue = .rejected("Couldn't read the Claude Code login from the Keychain")
            return report
        }
        if token.isExpired(now: now) {
            Log.auth.notice("claudeCode: login expired")
            report.issue = .expiredCLI
            return report
        }
        Log.auth.debug("claudeCode: login read")
        await callUsage(token: token.accessToken, report: &report,
                        rejected: .rejected("Claude Code login rejected — run `claude` once"))
        return report
    }

    // MARK: Claude desktop

    public func fetchDesktop(_ ctx: DesktopContext, now: Date) async -> FetchReport {
        var report = FetchReport()
        report.email = ctx.knownEmail
        let config: DesktopConfig
        do {
            config = try desktop.readConfig()
        } catch {
            report.issue = .notSignedIn("Claude app not found or not signed in")
            return report
        }
        guard !config.encryptedCaches.isEmpty else {
            report.issue = .notSignedIn("Not signed in to the Claude app")
            return report
        }
        // Never show Personal under Work: if the Claude app's account is the CLI's personal account, say so.
        let cli = ClaudeCodeAccountInfo.read()
        if let appUuid = config.lastKnownAccountUuid?.lowercased(), let personal = cli?.accountUuid?.lowercased(),
           appUuid == personal {
            report.issue = .signedInToPersonal("The Claude app")
            return report
        }
        guard ctx.allowKeychain || desktop.hasCachedKey else {
            report.issue = .notConnected
            return report
        }

        // Cookie first: claude.ai's usage endpoint has its own allowance, while the token route's OAuth
        // endpoint allows only a few requests, shared with Claude Code. The cookie is read once per fetch.
        var cookieIssue: AccountIssue?
        let sessionKey: String?
        do {
            sessionKey = try await Task.detached(priority: .utility) { [desktop] in try desktop.sessionKeyCookie() }.value
        } catch CredentialError.keychainDenied {
            report.issue = .keychainDenied
            return report
        } catch {
            sessionKey = nil
        }
        report.keychainOK = desktop.hasCachedKey
        if let sessionKey {
            report.route = "cookie"
            let (outcome, status) = await cookieClient.fetchUsage(cookies: ["sessionKey": sessionKey],
                                                                  personalOrg: cli?.organizationUuid)
            report.httpStatus = status
            AccountFetcher.applyCookieOutcome(outcome, report: &report, rejected: .expiredDesktop, now: Date())
            if AccountFetcher.desktopCookieResultStands(outcome) { return report }
            cookieIssue = report.issue
            report.issue = nil
        }

        var candidates: [TokenCandidate] = []
        do {
            candidates = try await Task.detached(priority: .utility) { [desktop] in
                try desktop.candidates(config: config)
            }.value
            report.keychainOK = true
        } catch CredentialError.keychainDenied {
            report.issue = .keychainDenied
            return report
        } catch CredentialError.notFound {
            report.issue = .notSignedIn("Claude Safe Storage not found — open the Claude app once")
            return report
        } catch {
            // Decrypt failed or unknown shape: no token to try.
            report.keychainOK = desktop.hasCachedKey
        }

        let accountUuid = config.lastKnownAccountUuid
        let cachedPath = accountUuid.flatMap { ctx.cachedChoice[$0] }
        let ordered = DesktopTokenParser.ordered(candidates, accountUuid: accountUuid,
                                                 cachedPath: cachedPath, now: now)
        var chosen: TokenCandidate?
        // Trusted pick: a candidate tied to the account uuid, or an unexpired cached path (re-verified below).
        var trustedChoice = false
        if let first = ordered.first, first.mentions(accountUuid) {
            chosen = first
            trustedChoice = true
        } else if let first = ordered.first, !first.isExpired(now: now), cachedPath == first.pathString {
            chosen = first
        } else {
            let (cand, matched) = await probe(ordered.filter { !$0.isExpired(now: now) }, accountUuid: accountUuid,
                                              personalOrg: cli?.organizationUuid)
            chosen = cand
            trustedChoice = matched
        }
        if chosen == nil, let anyExpired = ordered.first(where: { $0.isExpired(now: now) }), ordered.count == 1 {
            chosen = anyExpired
        }

        if let c = chosen {
            if c.isExpired(now: now) {
                report.issue = .expiredDesktop
                return report
            }
            // A uuid/profile match is trustworthy enough to cache before the usage call.
            if trustedChoice, let uuid = accountUuid { report.desktopChoice = (uuid, c.pathString) }
            report.route = "token"
            await callUsage(token: c.accessToken, report: &report, rejected: .expiredDesktop)
            if report.snapshot != nil {
                // A non-uuid-matched token (cached path or fallback) could belong to the personal org;
                // verify its profile before showing numbers, so Personal is never shown under Work.
                // A trusted pick needs the profile only for a missing email.
                var profile: Profile?
                if !trustedChoice || report.email == nil {
                    profile = (try? await client.fetchProfile(token: c.accessToken))?.0
                }
                if !trustedChoice, let personal = cli?.organizationUuid?.lowercased(),
                   let org = profile?.organizationUuid?.lowercased(), org == personal {
                    report.snapshot = nil
                    report.desktopChoice = nil
                    report.issue = .signedInToPersonal("The Claude app")
                    return report
                }
                // Save the choice after a confirmed 200, never for an expired/401/fallback/personal pick.
                if let uuid = accountUuid { report.desktopChoice = (uuid, c.pathString) }
                if report.email == nil { report.email = profile?.email }
                return report
            }
            // The token failed too (or there was no cookie): show the token route's issue.
            return report
        }
        // No token to try: show the cookie route's issue (the cookie was already tried above).
        report.issue = cookieIssue ?? .notSignedIn("No Claude app sign-in token found")
        return report
    }

    /// Whether a Claude app cookie result stands, or the token route is tried next. Numbers, a 429 (its
    /// wait applies) and a network error stand, as does "signed in to Personal" (never shown under Work).
    /// A rejected cookie, Cloudflare, a scope error and anything unexpected try the token.
    static func desktopCookieResultStands(_ outcome: CookieUsageOutcome) -> Bool {
        switch outcome {
        case .ok, .rateLimited, .network, .signedInToPersonal:
            return true
        case .unauthorized, .noSession, .noOrganizations, .cloudflare, .scopeError, .http, .undecodable:
            return false
        }
    }

    /// Picks the candidate whose profile matches the desktop account. Returns whether it matched by uuid.
    /// When the personal org is unknown, only a uuid match is accepted (never a "not personal" guess).
    private func probe(_ cands: [TokenCandidate], accountUuid: String?,
                       personalOrg: String?) async -> (TokenCandidate?, matchedByUuid: Bool) {
        guard !cands.isEmpty else { return (nil, false) }
        var notPersonal: TokenCandidate?
        for c in cands {
            guard let (profile, status) = try? await client.fetchProfile(token: c.accessToken), status == 200,
                  let p = profile else { continue }
            if let a = accountUuid, p.accountUuid?.lowercased() == a.lowercased() { return (c, true) }
            // Only accept a "not personal" fallback when we actually know the personal org.
            if notPersonal == nil, let personal = personalOrg?.lowercased(),
               let org = p.organizationUuid?.lowercased(), org != personal { notPersonal = c }
        }
        return (notPersonal, false)
    }

    private func callUsage(token: String, report: inout FetchReport, rejected: AccountIssue) async {
        do {
            let (outcome, status) = try await client.fetchUsage(token: token)
            report.httpStatus = status
            AccountFetcher.apply(outcome, report: &report, rejected: rejected, now: Date())
        } catch {
            report.issue = .network(AccountFetcher.shortNetworkMessage(error))
        }
    }

    static func apply(_ outcome: FetchOutcome, report: inout FetchReport, rejected: AccountIssue, now: Date,
                      jitter: TimeInterval? = nil) {
        switch outcome {
        case .ok(let snap):
            report.snapshot = snap
            report.issue = nil
        case .unauthorized:
            report.issue = rejected
        case .scopeError:
            report.issue = .scope
        case .rateLimited(let ra):
            rateLimited(ra, report: &report, now: now, jitter: jitter)
        case .http(let code):
            report.issue = .http(code)
        case .undecodable:
            report.issue = .rejected("Unexpected usage response")
        }
    }

    /// A 429 waits Retry-After (FetchPolicy.rateLimitWait) plus a jitter; nil draws one from the range.
    static func rateLimited(_ retryAfter: TimeInterval?, report: inout FetchReport, now: Date, jitter: TimeInterval?) {
        let j = jitter ?? FetchPolicy.rateLimitJitter()
        report.retryAfter = retryAfter
        report.rateLimitJitter = j
        report.issue = .rateLimited(until: now.addingTimeInterval(FetchPolicy.rateLimitWait(retryAfter) + j))
    }

    static func shortNetworkMessage(_ error: Error) -> String {
        guard let u = error as? URLError else { return "request failed" }
        switch u.code {
        case .notConnectedToInternet: return "offline"
        case .timedOut: return "timed out"
        case .cannotFindHost, .dnsLookupFailed: return "can't resolve host"
        case .networkConnectionLost: return "connection lost"
        case .secureConnectionFailed: return "TLS failed"
        default: return "request failed (\(u.code.rawValue))"
        }
    }
}
