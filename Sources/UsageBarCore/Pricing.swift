import Foundation

/// USD per million tokens.
public struct ModelPrice: Equatable, Sendable {
    public var input: Double
    public var output: Double
    public var cacheWrite5m: Double
    public var cacheWrite1h: Double
    public var cacheRead: Double
}

public struct TokenCounts: Codable, Equatable, Sendable {
    public var input: Int64 = 0
    public var output: Int64 = 0
    public var cacheWrite5m: Int64 = 0
    public var cacheWrite1h: Int64 = 0
    public var cacheRead: Int64 = 0

    public init(input: Int64 = 0, output: Int64 = 0, cacheWrite5m: Int64 = 0, cacheWrite1h: Int64 = 0, cacheRead: Int64 = 0) {
        self.input = input
        self.output = output
        self.cacheWrite5m = cacheWrite5m
        self.cacheWrite1h = cacheWrite1h
        self.cacheRead = cacheRead
    }

    public var total: Int64 { input + output + cacheWrite5m + cacheWrite1h + cacheRead }

    public static func + (a: TokenCounts, b: TokenCounts) -> TokenCounts {
        TokenCounts(input: a.input + b.input, output: a.output + b.output, cacheWrite5m: a.cacheWrite5m + b.cacheWrite5m,
                    cacheWrite1h: a.cacheWrite1h + b.cacheWrite1h, cacheRead: a.cacheRead + b.cacheRead)
    }

    public static func += (a: inout TokenCounts, b: TokenCounts) { a = a + b }
}

public enum Pricing {
    // Source: Anthropic pricing page https://platform.claude.com/docs/en/about-claude/pricing (fetched 2026-10-25),
    // cross-checked against the claude-api skill (Claude Code 2.1.310, model table cached 2026-10-06).
    // They agree on every figure except Sonnet 5.5 cache reads: the skill says $0.20; the live page says $0.10
    // (its footnote: cache hits on Opus 5.5 and Sonnet 5.5 are 0.05x base input, 0.05 x $2 = $0.10).
    // The live page wins here.
    // Haiku 5.5's >100K-prompt tier ($0.50 / $2.50) is deliberately ignored.
    public static let table: [String: ModelPrice] = [
        "claude-opus-5-5":   ModelPrice(input: 4.00,  output: 20.00, cacheWrite5m: 5.00,  cacheWrite1h: 8.00,  cacheRead: 0.20),
        "claude-sonnet-5-5": ModelPrice(input: 2.00,  output: 10.00, cacheWrite5m: 2.50,  cacheWrite1h: 4.00,  cacheRead: 0.10),
        "claude-haiku-5-5":  ModelPrice(input: 0.10,  output: 0.50,  cacheWrite5m: 0.125, cacheWrite1h: 0.20,  cacheRead: 0.01),
        "claude-fable-5-1":  ModelPrice(input: 10.00, output: 50.00, cacheWrite5m: 12.50, cacheWrite1h: 20.00, cacheRead: 0.25),
    ]

    /// Unknown models map by family substring to the current model of that family.
    static let families: [(String, String)] = [
        ("opus", "claude-opus-5-5"),
        ("sonnet", "claude-sonnet-5-5"),
        ("haiku", "claude-haiku-5-5"),
        ("fable", "claude-fable-5-1"),
    ]

    public enum Match: Equatable, Sendable {
        case exact(String)
        case family(String)
        case unpriced
    }

    public static func match(_ model: String) -> Match {
        let m = model.lowercased()
        if table[m] != nil { return .exact(m) }
        // Suffixed ids of a priced model, e.g. "claude-opus-5-5[1m]" or a dated snapshot.
        if let key = table.keys.sorted(by: { $0.count > $1.count }).first(where: { m.hasPrefix($0) }) { return .exact(key) }
        if let fam = families.first(where: { m.contains($0.0) }) { return .family(fam.1) }
        return .unpriced
    }

    public static func price(for model: String) -> ModelPrice? {
        switch match(model) {
        case .exact(let k), .family(let k): return table[k]
        case .unpriced: return nil
        }
    }

    /// USD cost, or nil for an unpriced model.
    public static func cost(_ c: TokenCounts, model: String) -> Double? {
        guard let p = price(for: model) else { return nil }
        let perToken = 1.0 / 1_000_000
        return (Double(c.input) * p.input + Double(c.output) * p.output + Double(c.cacheWrite5m) * p.cacheWrite5m
                + Double(c.cacheWrite1h) * p.cacheWrite1h + Double(c.cacheRead) * p.cacheRead) * perToken
    }
}
