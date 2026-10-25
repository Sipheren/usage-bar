import Foundation

/// A JSON document that keeps what `JSONSerialization` loses: the order of object keys and the exact
/// spelling of numbers (`33.0` stays `33.0`, `33` stays `33`). Used to rewrite API responses into
/// fixtures with the same structure and value types.
public indirect enum JSONValue: Equatable, Sendable {
    case object([JSONMember])
    case array([JSONValue])
    case string(String)
    /// The number exactly as written.
    case number(String)
    case bool(Bool)
    case null

    public struct ParseError: Error, Equatable {
        public var offset: Int
        public var reason: String
    }

    public static func parse(_ data: Data) throws -> JSONValue {
        var p = Parser(bytes: Array(data))
        p.skipWhitespace()
        let v = try p.value()
        p.skipWhitespace()
        guard p.i == p.bytes.count else { throw ParseError(offset: p.i, reason: "trailing characters") }
        return v
    }

    public static func parse(_ text: String) throws -> JSONValue { try parse(Data(text.utf8)) }

    /// Pretty-printed with two-space indents and a trailing newline. Slashes and non-ASCII are not escaped.
    public func serialized() -> String {
        var out = ""
        write(&out, indent: 0)
        out += "\n"
        return out
    }

    public subscript(key: String) -> JSONValue? {
        guard case .object(let members) = self else { return nil }
        return members.first { $0.key == key }?.value
    }

    public var stringValue: String? {
        if case .string(let s) = self { return s }
        return nil
    }

    public var doubleValue: Double? {
        if case .number(let n) = self { return Double(n) }
        return nil
    }

    // MARK: Writing

    private func write(_ out: inout String, indent: Int) {
        let pad = String(repeating: "  ", count: indent)
        let inner = String(repeating: "  ", count: indent + 1)
        switch self {
        case .object(let members):
            if members.isEmpty { out += "{}"; return }
            out += "{\n"
            for (n, m) in members.enumerated() {
                out += inner + JSONValue.quote(m.key) + ": "
                m.value.write(&out, indent: indent + 1)
                out += n == members.count - 1 ? "\n" : ",\n"
            }
            out += pad + "}"
        case .array(let items):
            if items.isEmpty { out += "[]"; return }
            out += "[\n"
            for (n, item) in items.enumerated() {
                out += inner
                item.write(&out, indent: indent + 1)
                out += n == items.count - 1 ? "\n" : ",\n"
            }
            out += pad + "]"
        case .string(let s): out += JSONValue.quote(s)
        case .number(let n): out += n
        case .bool(let b): out += b ? "true" : "false"
        case .null: out += "null"
        }
    }

    static func quote(_ s: String) -> String {
        var out = "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case "\u{08}": out += "\\b"
            case "\u{0C}": out += "\\f"
            default:
                if scalar.value < 0x20 {
                    out += String(format: "\\u%04x", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        return out + "\""
    }

    // MARK: Parsing

    private struct Parser {
        let bytes: [UInt8]
        var i = 0

        init(bytes: [UInt8]) { self.bytes = bytes }

        func fail(_ reason: String) -> ParseError { ParseError(offset: i, reason: reason) }

        mutating func skipWhitespace() {
            while i < bytes.count, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[i]) { i += 1 }
        }

        mutating func value() throws -> JSONValue {
            guard i < bytes.count else { throw fail("unexpected end") }
            switch bytes[i] {
            case UInt8(ascii: "{"): return try object()
            case UInt8(ascii: "["): return try array()
            case UInt8(ascii: "\""): return .string(try string())
            case UInt8(ascii: "t"): try literal("true"); return .bool(true)
            case UInt8(ascii: "f"): try literal("false"); return .bool(false)
            case UInt8(ascii: "n"): try literal("null"); return .null
            default: return .number(try number())
            }
        }

        mutating func literal(_ word: String) throws {
            let w = Array(word.utf8)
            guard i + w.count <= bytes.count, Array(bytes[i..<i + w.count]) == w else { throw fail("bad literal") }
            i += w.count
        }

        mutating func object() throws -> JSONValue {
            i += 1
            var members: [JSONMember] = []
            skipWhitespace()
            if i < bytes.count, bytes[i] == UInt8(ascii: "}") { i += 1; return .object(members) }
            while true {
                skipWhitespace()
                guard i < bytes.count, bytes[i] == UInt8(ascii: "\"") else { throw fail("expected key") }
                let key = try string()
                skipWhitespace()
                guard i < bytes.count, bytes[i] == UInt8(ascii: ":") else { throw fail("expected colon") }
                i += 1
                skipWhitespace()
                members.append(JSONMember(key: key, value: try value()))
                skipWhitespace()
                guard i < bytes.count else { throw fail("unexpected end") }
                if bytes[i] == UInt8(ascii: ",") { i += 1; continue }
                if bytes[i] == UInt8(ascii: "}") { i += 1; return .object(members) }
                throw fail("expected , or }")
            }
        }

        mutating func array() throws -> JSONValue {
            i += 1
            var items: [JSONValue] = []
            skipWhitespace()
            if i < bytes.count, bytes[i] == UInt8(ascii: "]") { i += 1; return .array(items) }
            while true {
                skipWhitespace()
                items.append(try value())
                skipWhitespace()
                guard i < bytes.count else { throw fail("unexpected end") }
                if bytes[i] == UInt8(ascii: ",") { i += 1; continue }
                if bytes[i] == UInt8(ascii: "]") { i += 1; return .array(items) }
                throw fail("expected , or ]")
            }
        }

        mutating func number() throws -> String {
            let start = i
            if i < bytes.count, bytes[i] == UInt8(ascii: "-") { i += 1 }
            func digits() { while i < bytes.count, (0x30...0x39).contains(bytes[i]) { i += 1 } }
            let intStart = i
            digits()
            guard i > intStart else { throw fail("bad number") }
            if i < bytes.count, bytes[i] == UInt8(ascii: ".") {
                i += 1
                let fracStart = i
                digits()
                guard i > fracStart else { throw fail("bad fraction") }
            }
            if i < bytes.count, bytes[i] == UInt8(ascii: "e") || bytes[i] == UInt8(ascii: "E") {
                i += 1
                if i < bytes.count, bytes[i] == UInt8(ascii: "+") || bytes[i] == UInt8(ascii: "-") { i += 1 }
                let expStart = i
                digits()
                guard i > expStart else { throw fail("bad exponent") }
            }
            return String(decoding: bytes[start..<i], as: UTF8.self)
        }

        mutating func hex4() throws -> UInt32 {
            guard i + 4 <= bytes.count, let v = UInt32(String(decoding: bytes[i..<i + 4], as: UTF8.self), radix: 16)
            else { throw fail("bad \\u escape") }
            i += 4
            return v
        }

        mutating func string() throws -> String {
            i += 1
            var out = [UInt8]()
            while i < bytes.count {
                let b = bytes[i]
                if b == UInt8(ascii: "\"") {
                    i += 1
                    return String(decoding: out, as: UTF8.self)
                }
                if b == UInt8(ascii: "\\") {
                    i += 1
                    guard i < bytes.count else { break }
                    let e = bytes[i]
                    i += 1
                    switch e {
                    case UInt8(ascii: "\""): out.append(0x22)
                    case UInt8(ascii: "\\"): out.append(0x5C)
                    case UInt8(ascii: "/"): out.append(0x2F)
                    case UInt8(ascii: "b"): out.append(0x08)
                    case UInt8(ascii: "f"): out.append(0x0C)
                    case UInt8(ascii: "n"): out.append(0x0A)
                    case UInt8(ascii: "r"): out.append(0x0D)
                    case UInt8(ascii: "t"): out.append(0x09)
                    case UInt8(ascii: "u"):
                        var code = try hex4()
                        // A surrogate pair spells one scalar outside the basic plane.
                        if (0xD800...0xDBFF).contains(code), i + 1 < bytes.count,
                           bytes[i] == UInt8(ascii: "\\"), bytes[i + 1] == UInt8(ascii: "u") {
                            i += 2
                            let low = try hex4()
                            code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00)
                        }
                        out.append(contentsOf: Array(String(Unicode.Scalar(code).map(Character.init) ?? "\u{FFFD}").utf8))
                    default: throw fail("bad escape")
                    }
                    continue
                }
                out.append(b)
                i += 1
            }
            throw fail("unterminated string")
        }
    }
}

public struct JSONMember: Equatable, Sendable {
    public var key: String
    public var value: JSONValue

    public init(key: String, value: JSONValue) {
        self.key = key
        self.value = value
    }
}
