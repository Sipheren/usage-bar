import XCTest
@testable import UsageBarCore

final class CostScannerTests: XCTestCase {
    var dir: URL!
    let utc = TimeZone(identifier: "UTC")!
    let now = Date()
    lazy var todayTS: String = iso(now.addingTimeInterval(-60))
    lazy var yesterdayTS: String = iso(now.addingTimeInterval(-86400))

    func iso(_ d: Date) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        f.timeZone = utc
        return f.string(from: d)
    }

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("usagebar-tests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir.appendingPathComponent("proj/sub"), withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    /// Builds one assistant log line.
    func line(id: String?, req: String?, session: String = "s1", model: String = "claude-opus-5-5", ts: String? = nil,
              input: Int = 0, output: Int = 0, read: Int? = nil, create: Int? = nil, split: (Int, Int)? = nil,
              stop: String? = "end_turn", reqKey: String = "requestId") -> String {
        var usage: [String: Any] = ["input_tokens": input, "output_tokens": output]
        if let r = read { usage["cache_read_input_tokens"] = r }
        if let c = create { usage["cache_creation_input_tokens"] = c }
        if let s = split { usage["cache_creation"] = ["ephemeral_5m_input_tokens": s.0, "ephemeral_1h_input_tokens": s.1] }
        var msg: [String: Any] = ["model": model, "usage": usage, "content": [["type": "text", "text": "hi"]]]
        if let id { msg["id"] = id }
        msg["stop_reason"] = stop ?? NSNull()
        var obj: [String: Any] = ["type": "assistant", "sessionId": session, "timestamp": ts ?? todayTS, "message": msg]
        if let req { obj[reqKey] = req }
        let d = try! JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys])
        return String(data: d, encoding: .utf8)!
    }

    func write(_ lines: [String], to name: String, trailingNewline: Bool = true) throws {
        let text = lines.joined(separator: "\n") + (trailingNewline ? "\n" : "")
        try text.write(to: dir.appendingPathComponent(name), atomically: false, encoding: .utf8)
    }

    func append(_ text: String, to name: String) throws {
        let h = try FileHandle(forWritingTo: dir.appendingPathComponent(name))
        try h.seekToEnd()
        try h.write(contentsOf: Data(text.utf8))
        try h.close()
    }

    func scanner() -> CostScanner {
        let s = CostScanner(root: dir, timeZone: utc)
        s.workers = 2
        return s
    }

    func testStreamingLinesKeepTheLargestCumulativeUsage() throws {
        try write([
            line(id: "m1", req: "r1", input: 100, output: 1, read: 0, create: 0, stop: nil),
            line(id: "m1", req: "r1", input: 100, output: 400, read: 0, create: 0),
            line(id: "m1", req: "r1", input: 100, output: 400, read: 0, create: 0),   // exact repeat
        ], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 500)
    }

    func testIncompleteRowNeverReplacesCompleteRow() throws {
        try write([
            line(id: "m1", req: "r1", input: 10, output: 5, read: 0, create: 0),
            // stop_reason null, input > 0, output 0, no cache fields: incomplete, even though its total is larger.
            line(id: "m1", req: "r1", input: 100, output: 0, stop: nil),
        ], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 15)
    }

    func testCompleteRowReplacesIncompleteRow() throws {
        try write([
            line(id: "m1", req: "r1", input: 100, output: 0, stop: nil),
            line(id: "m1", req: "r1", input: 10, output: 5, read: 0, create: 0),
        ], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 15)
    }

    func testCacheSplitIsUsedInsteadOfAggregate() throws {
        // Aggregate 1000 must not be added on top of the 600/400 split.
        try write([line(id: "m1", req: "r1", input: 0, output: 0, read: 0, create: 1000, split: (600, 400))],
                  to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        let sum = s.summary(now: now)
        XCTAssertEqual(sum.todayTokens, 1000)
        // opus-5-5: 600 x $5 + 400 x $8 per MTok
        XCTAssertEqual(sum.todayUSD, (600 * 5 + 400 * 8) / 1e6, accuracy: 1e-12)
    }

    func testAggregateCacheCreationCountsAsFiveMinuteWhenSplitMissing() throws {
        try write([line(id: "m1", req: "r1", input: 0, output: 0, read: 0, create: 1000)], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayUSD, 1000 * 5 / 1e6, accuracy: 1e-12)
    }

    func testPricingAcrossCategories() {
        let c = TokenCounts(input: 1_000_000, output: 1_000_000, cacheWrite5m: 1_000_000, cacheWrite1h: 1_000_000,
                            cacheRead: 1_000_000)
        XCTAssertEqual(Pricing.cost(c, model: "claude-opus-5-5")!, 4 + 20 + 5 + 8 + 0.20, accuracy: 1e-9)
        XCTAssertEqual(Pricing.cost(c, model: "claude-sonnet-5-5")!, 2 + 10 + 2.5 + 4 + 0.10, accuracy: 1e-9)
        XCTAssertEqual(Pricing.cost(c, model: "claude-haiku-5-5")!, 0.10 + 0.50 + 0.125 + 0.20 + 0.01, accuracy: 1e-9)
        XCTAssertEqual(Pricing.cost(c, model: "claude-fable-5-1")!, 10 + 50 + 12.5 + 20 + 0.25, accuracy: 1e-9)
    }

    func testUnknownModelsMapByFamilyOrStayUnpriced() {
        XCTAssertEqual(Pricing.match("claude-opus-5"), .family("claude-opus-5-5"))
        XCTAssertEqual(Pricing.match("claude-haiku-4-5-20251001"), .family("claude-haiku-5-5"))
        XCTAssertEqual(Pricing.match("fable"), .family("claude-fable-5-1"))
        XCTAssertEqual(Pricing.match("claude-opus-5-5[1m]"), .exact("claude-opus-5-5"))
        XCTAssertEqual(Pricing.match("lightning-128k"), .unpriced)
        XCTAssertNil(Pricing.cost(TokenCounts(input: 5), model: "lightning-128k"))
    }

    func testUnpricedModelCostsZeroAndIsListed() throws {
        try write([
            line(id: "m1", req: "r1", model: "lightning-128k", input: 1000, output: 10, read: 0, create: 0),
            line(id: "m2", req: "r2", model: "claude-sonnet-5-5", input: 1_000_000, output: 0, read: 0, create: 0),
        ], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        let sum = s.summary(now: now)
        XCTAssertEqual(sum.unpricedModels, ["lightning-128k"])
        XCTAssertEqual(sum.todayUSD, 2.0, accuracy: 1e-9)
        XCTAssertEqual(sum.topModel, "claude-sonnet-5-5")
        XCTAssertEqual(sum.todayTokens, 1_001_010)
    }

    func testFallbackKeyUsesSessionWhenRequestIdMissingAndAcceptsRequestIdAlias() throws {
        try write([
            line(id: "m1", req: nil, session: "sA", input: 10, output: 1, read: 0, create: 0),
            line(id: "m1", req: nil, session: "sB", input: 20, output: 1, read: 0, create: 0),   // different session
            line(id: "m2", req: "r9", input: 5, output: 1, read: 0, create: 0, reqKey: "request_id"),
            line(id: "m2", req: "r9", input: 5, output: 1, read: 0, create: 0),                   // same key via requestId
        ], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 11 + 21 + 6)
    }

    func testSkipsNonAssistantAndUnparseableLines() throws {
        let user = #"{"type":"user","message":{"content":"what is my \"usage\" today, assistant?"}}"#
        try write([user, "{not json \"usage\" \"assistant\"", "",
                   line(id: "m1", req: "r1", input: 7, output: 3, read: 0, create: 0)], to: "proj/a.jsonl")
        let s = scanner()
        let stats = s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 10)
        XCTAssertEqual(stats.uniqueKeys, 1)
    }

    func testDuplicatesAcrossFilesCountOnceAndSubfoldersAreScanned() throws {
        let l = line(id: "m1", req: "r1", input: 50, output: 50, read: 0, create: 0)
        try write([l], to: "proj/a.jsonl")
        try write([l], to: "proj/sub/agent-1.jsonl")
        let s = scanner()
        let stats = s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 100)
        XCTAssertEqual(stats.crossFileDuplicateKeys, 1)
        XCTAssertEqual(stats.filesRead, 2)
    }

    func testIncrementalAppendReadsOnlyNewBytesAndWaitsForPartialLine() throws {
        let first = line(id: "m1", req: "r1", input: 10, output: 10, read: 0, create: 0)
        let second = line(id: "m2", req: "r2", input: 1, output: 1, read: 0, create: 0)
        let half = second.prefix(second.count / 2)
        try write([first], to: "proj/a.jsonl")
        try append(String(half), to: "proj/a.jsonl")      // partial trailing line
        let s = scanner()
        s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 20)

        try append(String(second.dropFirst(half.count)) + "\n", to: "proj/a.jsonl")
        let stats = s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 22)
        XCTAssertLessThan(stats.bytesRead, Int64(first.count + second.count))   // did not re-read the first line

        // Unchanged file: nothing read.
        XCTAssertEqual(s.scan(now: now).filesRead, 0)
    }

    func testLaterLineInAppendSupersedesEarlierStreamingLine() throws {
        try write([line(id: "m1", req: "r1", input: 100, output: 1, read: 0, create: 0, stop: nil)], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 101)
        try append(line(id: "m1", req: "r1", input: 100, output: 900, read: 0, create: 0) + "\n", to: "proj/a.jsonl")
        s.scan(now: now)
        XCTAssertEqual(s.summary(now: now).todayTokens, 1000)
    }

    func testDailyBucketsAndThirtyDayTotals() throws {
        try write([
            line(id: "m1", req: "r1", ts: todayTS, input: 1_000_000, output: 0, read: 0, create: 0),
            line(id: "m2", req: "r2", ts: yesterdayTS, input: 2_000_000, output: 0, read: 0, create: 0),
            line(id: "m3", req: "r3", ts: iso(now.addingTimeInterval(-40 * 86400)), input: 9, output: 0, read: 0, create: 0),
        ], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        let sum = s.summary(now: now)
        XCTAssertEqual(sum.todayUSD, 4, accuracy: 1e-9)
        XCTAssertEqual(sum.last30USD, 12, accuracy: 1e-9)
        XCTAssertEqual(sum.last30Tokens, 3_000_000)
        XCTAssertEqual(sum.dailyUSD.count, 30)
        XCTAssertEqual(sum.dailyUSD[29], 4, accuracy: 1e-9)
        XCTAssertEqual(sum.dailyUSD[28], 8, accuracy: 1e-9)
    }

    func testCacheRoundTripKeepsTotalsAndAvoidsRescan() throws {
        try write([line(id: "m1", req: "r1", input: 10, output: 10, read: 5, create: 0, split: (3, 2))], to: "proj/a.jsonl")
        let s = scanner()
        s.scan(now: now)
        let exported = s.export()
        let encoded = try JSONEncoder().encode(CostCacheFile(version: CostCacheFile.currentVersion, roots: ["x": exported]))
        // Counts only: no message content or ids in the cache.
        let text = String(data: encoded, encoding: .utf8)!
        XCTAssertFalse(text.contains("m1"))
        XCTAssertFalse(text.contains("\"hi\""))

        let decoded = try JSONDecoder().decode(CostCacheFile.self, from: encoded)
        let s2 = scanner()
        s2.restore(decoded.roots["x"]!)
        XCTAssertEqual(s2.summary(now: now), s.summary(now: now))
        XCTAssertEqual(s2.scan(now: now).filesRead, 0)
    }

    func testFastTimestampParserMatchesFormatter() {
        for s in ["2026-10-25T07:41:12.345Z", "2026-01-01T00:00:00Z", "2024-02-29T23:59:59.999999Z"] {
            XCTAssertEqual(CostScanner.fastEpoch(s)!, ISODate.parse(s)!.timeIntervalSince1970, accuracy: 0.001, s)
        }
        XCTAssertNil(CostScanner.fastEpoch("2026-10-25T07:41:12+10:00"))
    }
}
