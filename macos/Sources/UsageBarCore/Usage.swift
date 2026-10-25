import Foundation

// MARK: - Normalised model

public enum LimitKind: Codable, Equatable, Hashable, Sendable {
    case session
    case weekly
    /// `id` distinguishes two scoped limits (e.g. by model id); `name` is the display name.
    case scoped(id: String, name: String)
}

public struct UsageLimit: Codable, Equatable, Identifiable, Sendable {
    public var kind: LimitKind
    public var percent: Double
    public var resetsAt: Date?
    public var severity: String?
    public var isActive: Bool?

    public init(kind: LimitKind, percent: Double, resetsAt: Date?, severity: String? = nil, isActive: Bool? = nil) {
        self.kind = kind
        self.percent = percent
        self.resetsAt = resetsAt
        self.severity = severity
        self.isActive = isActive
    }

    public var id: String {
        switch kind {
        case .session: return "session"
        case .weekly: return "weekly"
        case .scoped(let id, _): return "scoped:\(id)"
        }
    }

    public var name: String {
        switch kind {
        case .session: return "Session"
        case .weekly: return "Weekly"
        case .scoped(_, let name): return "\(name) weekly"
        }
    }

    /// Window length in minutes: session 300, weekly (all or scoped) 10080.
    public var windowMinutes: Double {
        if case .session = kind { return 300 }
        return 10080
    }

    public var isScoped: Bool {
        if case .scoped = kind { return true }
        return false
    }
}

public struct ExtraUsage: Codable, Equatable, Sendable {
    public var isEnabled: Bool?
    public var monthlyLimit: Double?
    public var usedCredits: Double?
    public var utilization: Double?
    public var currency: String?
    public var decimalPlaces: Int?
    public var disabledReason: String?

    public init(isEnabled: Bool?, monthlyLimit: Double?, usedCredits: Double?, utilization: Double?,
                currency: String?, decimalPlaces: Int?, disabledReason: String?) {
        self.isEnabled = isEnabled
        self.monthlyLimit = monthlyLimit
        self.usedCredits = usedCredits
        self.utilization = utilization
        self.currency = currency
        self.decimalPlaces = decimalPlaces
        self.disabledReason = disabledReason
    }
}

public struct UsageSnapshot: Codable, Equatable, Sendable {
    public var limits: [UsageLimit]
    public var extra: ExtraUsage?
    public var fetchedAt: Date

    public init(limits: [UsageLimit], extra: ExtraUsage?, fetchedAt: Date) {
        self.limits = limits
        self.extra = extra
        self.fetchedAt = fetchedAt
    }

    public var session: UsageLimit? { limits.first { $0.kind == .session } }
    public var weekly: UsageLimit? { limits.first { $0.kind == .weekly } }
    public var scoped: [UsageLimit] { limits.filter { $0.isScoped } }

    /// Display order: session, weekly, then scoped limits.
    public var ordered: [UsageLimit] {
        var out: [UsageLimit] = []
        if let s = session { out.append(s) }
        if let w = weekly { out.append(w) }
        out.append(contentsOf: scoped)
        return out
    }
}

// MARK: - Decoding

public enum UsageDecoder {
    public enum DecodeError: Error { case notAnObject }

    /// Decodes `/api/oauth/usage` (or the claude.ai org usage endpoint). Unknown keys and nulls are ignored.
    /// Prefers `limits[]`; falls back to `five_hour` / `seven_day` when it is absent or yields nothing.
    public static func decode(_ data: Data, fetchedAt: Date) throws -> UsageSnapshot {
        let raw: RawUsage
        do {
            raw = try JSONDecoder().decode(RawUsage.self, from: data)
        } catch {
            throw DecodeError.notAnObject
        }

        var limits: [UsageLimit] = []
        for entry in raw.limits ?? [] {
            guard let l = entry.value, let pct = l.percent ?? l.utilization else { continue }
            let resets = l.resetsAt.flatMap(ISODate.parse)
            let kind: LimitKind?
            let modelId = l.scope?.model?.id
            let scopeName = l.scope?.model?.displayName ?? modelId
            // Identity falls back to the name when there's no id; two scoped limits with distinct model
            // ids both show even if they share a display name.
            func scoped() -> LimitKind { .scoped(id: modelId ?? scopeName ?? "scoped", name: scopeName ?? "Scoped") }
            switch l.kind {
            case "session"?: kind = .session
            case "weekly_all"?: kind = .weekly
            case "weekly_scoped"?: kind = scoped()
            default:
                if l.group == "session" { kind = .session }
                else if l.group == "weekly" { kind = (scopeName != nil) ? scoped() : .weekly }
                else { kind = nil }
            }
            guard let k = kind, !limits.contains(where: { $0.kind == k }) else { continue }
            limits.append(UsageLimit(kind: k, percent: pct, resetsAt: resets, severity: l.severity, isActive: l.isActive))
        }

        if limits.isEmpty {
            if let f = raw.fiveHour, let u = f.utilization {
                limits.append(UsageLimit(kind: .session, percent: u, resetsAt: f.resetsAt.flatMap(ISODate.parse)))
            }
            if let s = raw.sevenDay, let u = s.utilization {
                limits.append(UsageLimit(kind: .weekly, percent: u, resetsAt: s.resetsAt.flatMap(ISODate.parse)))
            }
        }

        var extra: ExtraUsage?
        if let e = raw.extraUsage {
            extra = ExtraUsage(isEnabled: e.isEnabled, monthlyLimit: e.monthlyLimit, usedCredits: e.usedCredits,
                               utilization: e.utilization, currency: e.currency, decimalPlaces: e.decimalPlaces,
                               disabledReason: e.disabledReason)
        }
        return UsageSnapshot(limits: limits, extra: extra, fetchedAt: fetchedAt)
    }
}

/// Decodes T, or nil if that element is malformed, so one bad entry never sinks the response.
struct Lenient<T: Decodable>: Decodable {
    let value: T?
    init(from decoder: Decoder) throws { value = try? T(from: decoder) }
}

struct AnyKey: CodingKey {
    var stringValue: String
    var intValue: Int? { nil }
    init(_ s: String) { stringValue = s }
    init?(stringValue: String) { self.stringValue = stringValue }
    init?(intValue: Int) { return nil }
}

extension KeyedDecodingContainer where K == AnyKey {
    func number(_ key: String) -> Double? {
        let k = AnyKey(key)
        if let d = try? decode(Double.self, forKey: k) { return d }
        if let i = try? decode(Int.self, forKey: k) { return Double(i) }
        if let s = try? decode(String.self, forKey: k) { return Double(s) }
        return nil
    }
    func string(_ key: String) -> String? { try? decode(String.self, forKey: AnyKey(key)) }
    func bool(_ key: String) -> Bool? { try? decode(Bool.self, forKey: AnyKey(key)) }
    func nested<T: Decodable>(_ key: String, as: T.Type) -> T? { try? decode(T.self, forKey: AnyKey(key)) }
}

struct RawWindow: Decodable {
    let utilization: Double?
    let resetsAt: String?
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        utilization = c.number("utilization")
        resetsAt = c.string("resets_at")
    }
}

struct RawExtra: Decodable {
    let isEnabled: Bool?
    let monthlyLimit: Double?
    let usedCredits: Double?
    let utilization: Double?
    let currency: String?
    let decimalPlaces: Int?
    let disabledReason: String?
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        isEnabled = c.bool("is_enabled")
        monthlyLimit = c.number("monthly_limit")
        usedCredits = c.number("used_credits")
        utilization = c.number("utilization")
        currency = c.string("currency")
        decimalPlaces = c.number("decimal_places").map { Int($0) }
        disabledReason = c.string("disabled_reason")
    }
}

struct RawScopeModel: Decodable {
    let id: String?
    let displayName: String?
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        id = c.string("id")
        displayName = c.string("display_name")
    }
}

struct RawScope: Decodable {
    let model: RawScopeModel?
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        model = c.nested("model", as: RawScopeModel.self)
    }
}

struct RawLimit: Decodable {
    let kind: String?
    let group: String?
    let percent: Double?
    let utilization: Double?
    let severity: String?
    let resetsAt: String?
    let scope: RawScope?
    let isActive: Bool?
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        kind = c.string("kind")
        group = c.string("group")
        percent = c.number("percent")
        utilization = c.number("utilization")
        severity = c.string("severity")
        resetsAt = c.string("resets_at")
        scope = c.nested("scope", as: RawScope.self)
        isActive = c.bool("is_active")
    }
}

struct RawUsage: Decodable {
    let fiveHour: RawWindow?
    let sevenDay: RawWindow?
    let extraUsage: RawExtra?
    let limits: [Lenient<RawLimit>]?
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        fiveHour = c.nested("five_hour", as: RawWindow.self)
        sevenDay = c.nested("seven_day", as: RawWindow.self)
        extraUsage = c.nested("extra_usage", as: RawExtra.self)
        limits = c.nested("limits", as: [Lenient<RawLimit>].self)
    }
}

// MARK: - Profile

public struct Profile: Equatable, Sendable {
    public var email: String?
    public var displayName: String?
    public var accountUuid: String?
    public var organizationUuid: String?
    public var organizationName: String?
    public var organizationType: String?
    public var rateLimitTier: String?

    public init(email: String? = nil, displayName: String? = nil, accountUuid: String? = nil,
                organizationUuid: String? = nil, organizationName: String? = nil,
                organizationType: String? = nil, rateLimitTier: String? = nil) {
        self.email = email
        self.displayName = displayName
        self.accountUuid = accountUuid
        self.organizationUuid = organizationUuid
        self.organizationName = organizationName
        self.organizationType = organizationType
        self.rateLimitTier = rateLimitTier
    }

    public static func decode(_ data: Data) -> Profile? {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        let account = obj["account"] as? [String: Any] ?? [:]
        let org = obj["organization"] as? [String: Any] ?? [:]
        return Profile(email: account["email"] as? String ?? account["email_address"] as? String,
                       displayName: account["display_name"] as? String,
                       accountUuid: account["uuid"] as? String,
                       organizationUuid: org["uuid"] as? String,
                       organizationName: org["name"] as? String,
                       organizationType: org["organization_type"] as? String,
                       rateLimitTier: org["rate_limit_tier"] as? String)
    }
}

// MARK: - Dates

public enum ISODate {
    nonisolated(unsafe) private static let fractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    nonisolated(unsafe) private static let plain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    private static let lock = NSLock()

    /// Parses ISO 8601 with or without fractional seconds (any number of digits) and an offset or Z.
    public static func parse(_ s: String) -> Date? {
        lock.lock(); defer { lock.unlock() }
        if let d = fractional.date(from: s) { return d }
        if let d = plain.date(from: s) { return d }
        // Some formatter versions reject more than 3 fractional digits: trim to 3 and retry.
        if let dot = s.firstIndex(of: ".") {
            var digitsEnd = s.index(after: dot)
            while digitsEnd < s.endIndex, s[digitsEnd].isNumber { digitsEnd = s.index(after: digitsEnd) }
            let digits = s[s.index(after: dot)..<digitsEnd]
            let trimmed = String(s[..<dot]) + "." + String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
                + String(s[digitsEnd...])
            if let d = fractional.date(from: trimmed) { return d }
        }
        return nil
    }
}
