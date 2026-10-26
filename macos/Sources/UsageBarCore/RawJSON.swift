import Foundation

/// Any JSON value, kept as decoded so it can be written back. Holds saved accounts this build can't
/// read. Values survive by meaning: an encoder may reorder object keys and respell numbers.
public indirect enum RawJSON: Codable, Equatable, Sendable {
    case object([String: RawJSON])
    case array([RawJSON])
    case string(String)
    case int(Int64)
    case double(Double)
    case bool(Bool)
    case null

    public init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        // Bool before the numbers so true/false never read as 1/0; Int64 before Double so ids keep every digit.
        if c.decodeNil() { self = .null }
        else if let v = try? c.decode(Bool.self) { self = .bool(v) }
        else if let v = try? c.decode(Int64.self) { self = .int(v) }
        else if let v = try? c.decode(Double.self) { self = .double(v) }
        else if let v = try? c.decode(String.self) { self = .string(v) }
        else if let v = try? c.decode([RawJSON].self) { self = .array(v) }
        else { self = .object(try c.decode([String: RawJSON].self)) }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .object(let v): try c.encode(v)
        case .array(let v): try c.encode(v)
        case .string(let v): try c.encode(v)
        case .int(let v): try c.encode(v)
        case .double(let v): try c.encode(v)
        case .bool(let v): try c.encode(v)
        case .null: try c.encodeNil()
        }
    }
}
