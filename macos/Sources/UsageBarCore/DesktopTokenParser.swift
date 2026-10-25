import Foundation

/// An access token found somewhere inside the desktop app's decrypted token cache.
/// `description` never includes the token itself.
public struct TokenCandidate: Sendable, CustomStringConvertible {
    /// Key path inside the cache (dictionary keys and array indices). Holds no secret values.
    public var path: [String]
    public var accessToken: String
    public var expiresAt: Date?
    /// Lowercased UUIDs mentioned in the path or alongside the token.
    public var uuids: Set<String>

    public var pathString: String { path.joined(separator: " › ") }

    public func isExpired(now: Date) -> Bool {
        guard let e = expiresAt else { return false }
        return e <= now
    }

    public func mentions(_ uuid: String?) -> Bool {
        guard let u = uuid?.lowercased(), !u.isEmpty else { return false }
        return uuids.contains(u)
    }

    public var description: String {
        "TokenCandidate(path: \(DesktopTokenParser.maskedPath(path)), tokenLength: \(accessToken.count), "
            + "expiresAt: \(expiresAt.map { ISO8601DateFormatter().string(from: $0) } ?? "none"), uuids: \(uuids.count))"
    }
}

public enum DesktopTokenParser {
    static let tokenKeys = ["accessToken", "access_token", "token"]
    static let expiryKeys = ["expiresAt", "expires_at", "expiry", "expires", "expiration", "expirationTime",
                             "expiresOn", "expiry_date", "exp"]

    // MARK: Candidates

    /// Walks any JSON value for objects carrying an access token. Embedded JSON strings are parsed too.
    public static func candidates(in json: Any) -> [TokenCandidate] {
        var out: [TokenCandidate] = []
        walk(json, path: [], inherited: [], depth: 0, out: &out)
        return out
    }

    public static func candidates(inJSONData data: Data) -> [TokenCandidate] {
        guard let json = parseJSON(data) else { return [] }
        return candidates(in: json)
    }

    static func parseJSON(_ data: Data) -> Any? {
        if let v = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) {
            if let s = v as? String, let inner = s.data(using: .utf8),
               let nested = try? JSONSerialization.jsonObject(with: inner) { return nested }
            return v
        }
        return nil
    }

    private static func walk(_ node: Any, path: [String], inherited: Set<String>, depth: Int, out: inout [TokenCandidate]) {
        guard depth < 32 else { return }
        if let dict = node as? [String: Any] {
            var context = inherited
            if let last = path.last { context.formUnion(uuids(in: last)) }
            for (_, v) in dict {
                if let s = v as? String { context.formUnion(uuids(in: s, wholeOnly: true)) }
            }
            for key in tokenKeys {
                if let t = dict[key] as? String, t.count >= 20 {
                    out.append(TokenCandidate(path: path + [key], accessToken: t, expiresAt: expiry(in: dict),
                                              uuids: context))
                }
            }
            for key in dict.keys.sorted() {
                guard let v = dict[key] else { continue }
                if tokenKeys.contains(key), v is String { continue }
                walkChild(v, path: path + [key], inherited: context, depth: depth, out: &out)
            }
        } else if let arr = node as? [Any] {
            for (i, v) in arr.enumerated() {
                walkChild(v, path: path + ["[\(i)]"], inherited: inherited, depth: depth, out: &out)
            }
        }
    }

    private static func walkChild(_ v: Any, path: [String], inherited: Set<String>, depth: Int, out: inout [TokenCandidate]) {
        if let s = v as? String {
            let trimmed = s.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("{") || trimmed.hasPrefix("["),
               let d = trimmed.data(using: .utf8), let nested = try? JSONSerialization.jsonObject(with: d) {
                walk(nested, path: path, inherited: inherited, depth: depth + 1, out: &out)
            }
        } else {
            walk(v, path: path, inherited: inherited, depth: depth + 1, out: &out)
        }
    }

    static func expiry(in dict: [String: Any]) -> Date? {
        for key in expiryKeys {
            guard let v = dict[key] else { continue }
            if let n = v as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID() { return epochDate(n.doubleValue) }
            if let s = v as? String {
                if let d = Double(s) { return epochDate(d) }
                if let d = ISODate.parse(s) { return d }
            }
        }
        return nil
    }

    static func epochDate(_ v: Double) -> Date? {
        if v > 1e14 { return Date(timeIntervalSince1970: v / 1e6) }   // microseconds
        if v > 1e11 { return Date(timeIntervalSince1970: v / 1000) }  // milliseconds
        if v > 1e8 { return Date(timeIntervalSince1970: v) }          // seconds
        return nil
    }

    static let uuidRegex = try! NSRegularExpression(
        pattern: "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")

    static func uuids(in s: String, wholeOnly: Bool = false) -> Set<String> {
        if wholeOnly {
            return s.count == 36 && UUID(uuidString: s) != nil ? [s.lowercased()] : []
        }
        let ns = s as NSString
        let matches = uuidRegex.matches(in: s, range: NSRange(location: 0, length: ns.length))
        return Set(matches.map { ns.substring(with: $0.range).lowercased() })
    }

    // MARK: Choosing

    /// Orders candidates for the desktop account: a uuid match first, then the cached choice only while
    /// it is still unexpired, then the rest. An expired cached path drops to the general group, where an
    /// unexpired candidate always outranks it (so a live token never loses to an expired cached one).
    public static func ordered(_ cands: [TokenCandidate], accountUuid: String?, cachedPath: String?, now: Date) -> [TokenCandidate] {
        func rank(_ c: TokenCandidate) -> Int {
            if c.mentions(accountUuid) { return 0 }
            if let p = cachedPath, c.pathString == p, !c.isExpired(now: now) { return 1 }
            return 2
        }
        return cands.sorted { a, b in
            let ra = rank(a), rb = rank(b)
            if ra != rb { return ra < rb }
            let ea = a.isExpired(now: now), eb = b.isExpired(now: now)
            if ea != eb { return !ea }                       // unexpired before expired within the group
            return (a.expiresAt ?? .distantPast) > (b.expiresAt ?? .distantPast)
        }
    }

    // MARK: Shape dump (key names and types only)

    /// Renders key names and value types, never values. UUIDs in keys are shortened; opaque runs are masked.
    public static func shape(of json: Any, indent: Int = 0) -> String {
        var lines: [String] = []
        shape(json, indent: indent, depth: 0, lines: &lines)
        return lines.joined(separator: "\n")
    }

    private static func shape(_ node: Any, indent: Int, depth: Int, lines: inout [String]) {
        let pad = String(repeating: "  ", count: indent)
        guard depth < 24 else { lines.append(pad + "…"); return }
        if let dict = node as? [String: Any] {
            for key in dict.keys.sorted() {
                let v = dict[key]!
                lines.append(pad + maskedKey(key) + ": " + typeDescription(v))
                if let s = v as? String, let nested = embeddedJSON(s) {
                    shape(nested, indent: indent + 1, depth: depth + 1, lines: &lines)
                } else if v is [String: Any] || v is [Any] {
                    shape(v, indent: indent + 1, depth: depth + 1, lines: &lines)
                }
            }
        } else if let arr = node as? [Any], let first = arr.first {
            lines.append(pad + "[0]: " + typeDescription(first) + (arr.count > 1 ? "  (showing first of \(arr.count))" : ""))
            if first is [String: Any] || first is [Any] {
                shape(first, indent: indent + 1, depth: depth + 1, lines: &lines)
            }
        }
    }

    static func embeddedJSON(_ s: String) -> Any? {
        let t = s.trimmingCharacters(in: .whitespaces)
        guard t.hasPrefix("{") || t.hasPrefix("["), let d = t.data(using: .utf8) else { return nil }
        return try? JSONSerialization.jsonObject(with: d)
    }

    public static func typeDescription(_ v: Any) -> String {
        switch v {
        case let d as [String: Any]: return "object(\(d.count) keys)"
        case let a as [Any]: return "array(\(a.count))"
        case let s as String:
            var hints: [String] = []
            if embeddedJSON(s) != nil { hints.append("embedded JSON") }
            else if UUID(uuidString: s) != nil { hints.append("uuid") }
            else if ISODate.parse(s) != nil { hints.append("ISO date") }
            else if s.hasPrefix("sk-ant-") { hints.append("sk-ant token") }
            else if s.split(separator: ".").count == 3 && s.count > 40 { hints.append("JWT-like") }
            else if s.count >= 32 && !s.contains(" ") { hints.append("opaque") }
            return "string(len \(s.count)" + (hints.isEmpty ? "" : ", " + hints.joined(separator: ", ")) + ")"
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return "bool" }
            let d = n.doubleValue
            var t = d == d.rounded() ? "number(int)" : "number(float)"
            if d > 1e11 && d < 1e14 { t += " epoch-ms-like" } else if d > 1e8 && d < 1e11 { t += " epoch-s-like" }
            return t
        case is NSNull: return "null"
        default: return String(describing: type(of: v))
        }
    }

    public static func maskedKey(_ key: String) -> String {
        if key.hasPrefix("sk-ant") { return "<secret-like key>" }
        let ns = key as NSString
        var out = ""
        var last = 0
        for m in uuidRegex.matches(in: key, range: NSRange(location: 0, length: ns.length)) {
            out += maskOpaque(ns.substring(with: NSRange(location: last, length: m.range.location - last)))
            out += "<uuid \(ns.substring(with: NSRange(location: m.range.location, length: 8)).lowercased())…>"
            last = m.range.location + m.range.length
        }
        out += maskOpaque(ns.substring(from: last))
        return out
    }

    /// Replaces long runs of token-ish characters (32+) with a length marker.
    static func maskOpaque(_ s: String) -> String {
        var out = ""
        var run = ""
        func flush() {
            out += run.count >= 32 ? "<opaque len \(run.count)>" : run
            run = ""
        }
        for ch in s {
            if ch.isLetter || ch.isNumber || ch == "_" || ch == "-" || ch == "=" || ch == "+" || ch == "/" { run.append(ch) }
            else { flush(); out.append(ch) }
        }
        flush()
        return out
    }

    public static func maskedPath(_ path: [String]) -> String {
        path.map(maskedKey).joined(separator: " › ")
    }
}
