import Foundation

/// Rewrites a real API response into a shareable fixture: the same keys, nesting, arrays, nulls and value
/// types, with every value replaced. Uuids map to `00000000-0000-4000-8000-0000000000NN` (the same input
/// always gives the same output, so cross-references survive), emails and names to example values,
/// timestamps to fixed times around `now`, numbers to made-up values that never equal any number in the
/// input. Booleans, nulls and enum-like contract strings (`kind`, `group`, `severity`, `currency`, plan
/// types…) stay. `decimal_places` stays too: it describes the currency, not the user.
public struct FixtureAnonymizer {
    public enum Document: String, CaseIterable, Sendable {
        case oauthUsage, oauthProfile, claudeAiOrganizations, claudeAiOrgUsage

        var owner: Owner { self == .oauthUsage || self == .oauthProfile ? .personal : .work }
    }

    enum Owner { case personal, work }

    /// Which usage window a value belongs to; decides its fake percentage and reset time.
    enum Window { case session, weekly, scoped, extra }

    /// The "now" every fake time is relative to.
    public static let now = ISODate.parse("2026-03-02T05:00:00Z")!
    public static let personalEmail = "personal@example.com"
    public static let workEmail = "work@example.com"
    public static let personalName = "Example Personal"
    public static let workName = "Example Co"
    public static let placeholder = "example"

    /// Keys whose string values are part of the API contract. Kept only when they look like an enum value.
    public static let enumKeys: Set<String> = [
        "kind", "group", "severity", "currency", "type", "status", "role", "tier", "plan", "plan_type",
        "organization_type", "billing_type", "rate_limit_tier", "disabled_reason", "subscription_status",
        "subscription_type", "capabilities", "raven_type", "seat_tier", "api_disabled_reason",
        "organization_role", "account_type", "payment_status",
    ]
    /// Numbers kept as they are: a property of the currency, not data about the user.
    public static let keptNumberKeys: Set<String> = ["decimal_places"]

    public struct Report: Sendable {
        /// "path = value" for every string kept because it looked like an enum (safe to show).
        public var keptValues: [String] = []
        /// Paths whose values were replaced (paths only, never values).
        public var replacedPaths = Set<String>()
    }

    public private(set) var report = Report()
    private let personalIDs: Set<String>
    private var uuidMap: [String: String] = [:]
    private var nextNumber: Int
    /// Raw string values kept as enums, so the leak check can allow exactly those.
    private var keptRaw = Set<String>()

    /// `personalIDs`: uuids (any case) of the personal account and its organization, so names inside
    /// those objects read "Example Personal" even in a work document. `firstNumber`: the first fake uuid
    /// number to hand out, so a run that adds files to a folder never reuses a fake id already in it.
    public init(personalIDs: Set<String> = [], firstNumber: Int = 1) {
        self.personalIDs = Set(personalIDs.map { $0.lowercased() })
        nextNumber = max(1, firstNumber)
    }

    /// The highest fake uuid number in `text` (0 if none), to pass on as `firstNumber - 1`.
    public static func highestFakeNumber(in text: String) -> Int {
        let re = try! NSRegularExpression(pattern: "00000000-0000-4000-8000-([0-9]{12})")
        let ns = text as NSString
        return re.matches(in: text, range: NSRange(location: 0, length: ns.length))
            .compactMap { Int(ns.substring(with: $0.range(at: 1))) }.max() ?? 0
    }

    // MARK: Rewriting

    private struct Context {
        var key: String?
        var path: String
        var window: Window?
        var owner: Owner
        var inModel = false
        var rawNumbers: Set<Double>
    }

    public mutating func anonymize(_ value: JSONValue, document: Document) -> JSONValue {
        var numbers = Set<Double>()
        FixtureAnonymizer.collectNumbers(value, into: &numbers)
        let ctx = Context(key: nil, path: "$", window: nil, owner: document.owner, rawNumbers: numbers)
        return rewrite(value, ctx)
    }

    private mutating func rewrite(_ value: JSONValue, _ ctx: Context) -> JSONValue {
        switch value {
        case .object(let members):
            var inner = ctx
            inner.window = FixtureAnonymizer.window(key: ctx.key, members: members) ?? ctx.window
            if let id = members.first(where: { $0.key == "uuid" || $0.key == "id" })?.value.stringValue,
               personalIDs.contains(id.lowercased()) {
                inner.owner = .personal
            }
            var out: [JSONMember] = []
            for m in members {
                var c = inner
                c.key = m.key
                c.path = ctx.path + "." + m.key
                c.inModel = ctx.key == "model"
                out.append(JSONMember(key: rewriteKey(m.key, owner: inner.owner), value: rewrite(m.value, c)))
            }
            return .object(out)
        case .array(let items):
            var c = ctx
            c.path = ctx.path + "[]"
            return .array(items.map { rewrite($0, c) })
        case .string(let s):
            return .string(rewriteString(s, ctx))
        case .number(let n):
            return .number(rewriteNumber(n, ctx))
        case .bool, .null:
            return value
        }
    }

    private mutating func rewriteKey(_ key: String, owner: Owner) -> String {
        if FixtureAnonymizer.isUUID(key) { return mapUUID(key) }
        if key.contains("@") { return owner == .personal ? FixtureAnonymizer.personalEmail : FixtureAnonymizer.workEmail }
        return key
    }

    private mutating func rewriteString(_ s: String, _ ctx: Context) -> String {
        let key = ctx.key ?? ""
        if FixtureAnonymizer.isUUID(s) {
            report.replacedPaths.insert(ctx.path)
            return mapUUID(s)
        }
        if FixtureAnonymizer.enumKeys.contains(key), FixtureAnonymizer.looksLikeEnum(s, key: key) {
            keep(s, ctx)
            return s
        }
        if ctx.inModel, ["id", "display_name", "name"].contains(key), FixtureAnonymizer.looksLikeModelName(s) {
            keep(s, ctx)
            return s
        }
        report.replacedPaths.insert(ctx.path)
        if let style = TimestampStyle(s) {
            let date = key.contains("reset") ? FixtureAnonymizer.fakeReset(ctx.window) : FixtureAnonymizer.fakePast
            return style.format(date)
        }
        if s.range(of: #"^\d{4}-\d{2}-\d{2}$"#, options: .regularExpression) != nil { return "2026-01-31" }
        // A name wins over an email inside it ("someone@…'s Organization" is still a name).
        if FixtureAnonymizer.isNameKey(key) {
            return ctx.owner == .personal ? FixtureAnonymizer.personalName : FixtureAnonymizer.workName
        }
        if s.contains("@") { return ctx.owner == .personal ? FixtureAnonymizer.personalEmail : FixtureAnonymizer.workEmail }
        return FixtureAnonymizer.placeholder
    }

    private mutating func keep(_ s: String, _ ctx: Context) {
        keptRaw.insert(s)
        report.keptValues.append("\(ctx.path) = \(s)")
    }

    private mutating func rewriteNumber(_ lexeme: String, _ ctx: Context) -> String {
        let key = ctx.key ?? ""
        if FixtureAnonymizer.keptNumberKeys.contains(key) { return lexeme }
        report.replacedPaths.insert(ctx.path)
        var fake = FixtureAnonymizer.fakeNumber(key: key, window: ctx.window)
        let isFloat = lexeme.contains(".") || lexeme.lowercased().contains("e")
        let digits = isFloat ? max(1, FixtureAnonymizer.fractionDigits(lexeme)) : 0
        func spelled(_ v: Double) -> String { isFloat ? String(format: "%.\(digits)f", v) : String(Int(v.rounded())) }
        // Never reuse a number that appears anywhere in the input.
        while ctx.rawNumbers.contains(Double(spelled(fake)) ?? fake) { fake += 1 }
        return spelled(fake)
    }

    private mutating func mapUUID(_ raw: String) -> String {
        let k = raw.lowercased()
        if let m = uuidMap[k] { return m }
        let fake = String(format: "00000000-0000-4000-8000-%012d", nextNumber)
        nextNumber += 1
        uuidMap[k] = fake
        return fake
    }

    // MARK: Rules

    static func window(key: String?, members: [JSONMember]) -> Window? {
        if let key {
            if key == "five_hour" { return .session }
            if key == "seven_day" { return .weekly }
            if key.hasPrefix("seven_day_") { return .scoped }
            if key == "extra_usage" { return .extra }
        }
        func member(_ k: String) -> String? { members.first { $0.key == k }?.value.stringValue }
        switch member("kind") {
        case "session"?: return .session
        case "weekly_all"?: return .weekly
        case "weekly_scoped"?: return .scoped
        default: break
        }
        switch member("group") {
        case "session"?: return .session
        case "weekly"?:
            let hasScope = members.contains { $0.key == "scope" && $0.value != .null }
            return hasScope ? .scoped : .weekly
        default: return nil
        }
    }

    static func fakeNumber(key: String, window: Window?) -> Double {
        let k = key.lowercased()
        if ["utilization", "percent", "percentage"].contains(k) || k.hasSuffix("_percent") || k.hasSuffix("_utilization") {
            switch window {
            case .session?: return 33
            case .weekly?: return 61
            case .scoped?: return 7
            case .extra?: return 12.5
            case nil: return 25
            }
        }
        if k == "monthly_limit" { return 10000 }
        if k == "used_credits" { return 1250 }
        if ["credit", "amount", "balance", "spend", "price", "cost", "cents", "limit"].contains(where: { k.contains($0) }) {
            return 500
        }
        return 1
    }

    /// Session resets in 2h 10m, weekly in 3 days 10 hours, a model-scoped weekly in 5 days.
    static func fakeReset(_ window: Window?) -> Date {
        switch window {
        case .session?: return now.addingTimeInterval(130 * 60)
        case .weekly?: return now.addingTimeInterval((3 * 24 + 10) * 3600)
        case .scoped?: return now.addingTimeInterval(5 * 24 * 3600)
        case .extra?, nil: return now.addingTimeInterval(30 * 24 * 3600)
        }
    }

    static let fakePast = now.addingTimeInterval(-30 * 24 * 3600)

    static func isUUID(_ s: String) -> Bool {
        s.range(of: #"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$"#,
                options: .regularExpression) != nil
    }

    static func looksLikeEnum(_ s: String, key: String) -> Bool {
        if key == "currency" { return s.range(of: #"^[A-Z]{3}$"#, options: .regularExpression) != nil }
        return s.range(of: #"^[a-z0-9_.:-]{1,64}$"#, options: .regularExpression) != nil
    }

    /// Public model names and ids such as "Fable" or "claude-fable-5-1".
    static func looksLikeModelName(_ s: String) -> Bool {
        s.range(of: #"^[A-Za-z][A-Za-z0-9 ._-]{0,39}$"#, options: .regularExpression) != nil && !s.contains("@")
    }

    static func isNameKey(_ key: String) -> Bool {
        ["name", "full_name", "display_name", "first_name", "last_name", "nickname"].contains(key) || key.hasSuffix("_name")
    }

    static func fractionDigits(_ lexeme: String) -> Int {
        guard let dot = lexeme.firstIndex(of: ".") else { return 0 }
        return lexeme[lexeme.index(after: dot)...].prefix { $0.isNumber }.count
    }

    static func collectNumbers(_ v: JSONValue, into set: inout Set<Double>) {
        switch v {
        case .number(let n): if let d = Double(n) { set.insert(d) }
        case .object(let ms): ms.forEach { collectNumbers($0.value, into: &set) }
        case .array(let items): items.forEach { collectNumbers($0, into: &set) }
        default: break
        }
    }

    // MARK: Leak check

    public struct LeakCheck: Equatable, Sendable {
        /// Input strings found in a value this anonymiser wrote (other than kept enum values).
        public var strings: Int
        /// Input numbers found anywhere in the output, other than `decimal_places`.
        public var numbers: Int
        /// Where the surviving input strings were, as paths (never values).
        public var paths: [String]
        public var clean: Bool { strings == 0 && numbers == 0 }
    }

    /// Counts input values that survived into a value the anonymiser wrote: a replacement string, a mapped
    /// key, or a number. Keys and kept enum values are part of the structure by design, and the fixed
    /// replacements ("example", "Example Co", the example.com addresses) carry nothing from the input even
    /// if an input value happens to read the same. What's left is the bug this guards against: an input
    /// value copied into the output. Returns counts and paths only, never values.
    public func leaks(raw: JSONValue, anonymized: JSONValue) -> LeakCheck {
        var rawStrings: [(path: String, value: String)] = []
        FixtureAnonymizer.collectStrings(raw, path: "$", into: &rawStrings)
        var outStrings: [(path: String, value: String)] = []
        FixtureAnonymizer.collectStrings(anonymized, path: "$", into: &outStrings)
        let written = Set(outStrings.map(\.value)).subtracting(keptRaw).subtracting(FixtureAnonymizer.constants)
        var paths = Set<String>()
        var hits = Set<String>()
        for (path, value) in rawStrings where !keptRaw.contains(value) && !FixtureAnonymizer.constants.contains(value) {
            let exact = written.contains(value)
            let inside = value.count >= 8 && written.contains { $0.contains(value) }
            if exact || inside {
                hits.insert(value)
                paths.insert(path)
            }
        }
        var rawNumbers = Set<Double>(), outNumbers = Set<Double>()
        var ignored = Set<String>()
        FixtureAnonymizer.collectLeaves(raw, strings: &ignored, numbers: &rawNumbers, mappedKeysOnly: true)
        FixtureAnonymizer.collectLeaves(anonymized, strings: &ignored, numbers: &outNumbers, mappedKeysOnly: true)
        return LeakCheck(strings: hits.count, numbers: rawNumbers.intersection(outNumbers).count, paths: paths.sorted())
    }

    static let constants: Set<String> = [placeholder, personalName, workName, personalEmail, workEmail]

    /// String values with their paths, plus object keys that the anonymiser rewrites (uuids, emails).
    static func collectStrings(_ v: JSONValue, path: String, into out: inout [(path: String, value: String)]) {
        switch v {
        case .string(let s): out.append((path, s))
        case .object(let ms):
            for m in ms {
                if isUUID(m.key) || m.key.contains("@") { out.append((path + ".<key>", m.key)) }
                collectStrings(m.value, path: path + "." + (isUUID(m.key) || m.key.contains("@") ? "<key>" : m.key), into: &out)
            }
        case .array(let items): items.forEach { collectStrings($0, path: path + "[]", into: &out) }
        case .number, .bool, .null: break
        }
    }

    /// Leaf strings and numbers. Keys count only when they would be rewritten (uuids, emails).
    static func collectLeaves(_ v: JSONValue, strings: inout Set<String>, numbers: inout Set<Double>,
                              mappedKeysOnly: Bool, key: String? = nil) {
        switch v {
        case .string(let s): strings.insert(s)
        case .number(let n):
            if let key, keptNumberKeys.contains(key) { return }
            if let d = Double(n) { numbers.insert(d) }
        case .object(let ms):
            for m in ms {
                if !mappedKeysOnly || isUUID(m.key) || m.key.contains("@") { strings.insert(m.key) }
                collectLeaves(m.value, strings: &strings, numbers: &numbers, mappedKeysOnly: mappedKeysOnly, key: m.key)
            }
        case .array(let items):
            items.forEach { collectLeaves($0, strings: &strings, numbers: &numbers, mappedKeysOnly: mappedKeysOnly, key: key) }
        case .bool, .null: break
        }
    }
}

/// The spelling of an ISO timestamp (fraction digits, `Z` or `+00:00`), so a fake keeps the same form.
struct TimestampStyle {
    var fractionDigits: Int
    var offset: String

    init?(_ s: String) {
        guard s.range(of: #"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(\.\d+)?(Z|[+-]\d{2}:?\d{2})$"#,
                      options: .regularExpression) != nil else { return nil }
        if let dot = s.firstIndex(of: ".") {
            fractionDigits = s[s.index(after: dot)...].prefix { $0.isNumber }.count
        } else {
            fractionDigits = 0
        }
        if s.hasSuffix("Z") {
            offset = "Z"
        } else {
            offset = String(s.suffix(s.contains(":", after: 19) ? 6 : 5))
        }
    }

    /// Writes `date` in UTC with this spelling (any offset is written as zero, e.g. `+00:00`).
    func format(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        var s = f.string(from: date)
        if fractionDigits > 0 { s += "." + String(repeating: "0", count: fractionDigits) }
        if offset == "Z" { return s + "Z" }
        return s + (offset.contains(":") ? "+00:00" : "+0000")
    }
}

private extension String {
    func contains(_ c: Character, after position: Int) -> Bool {
        guard count > position else { return false }
        return self[index(startIndex, offsetBy: position)...].contains(c)
    }
}
