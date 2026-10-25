import Foundation

// MARK: - Public results

public struct ModelCost: Equatable, Sendable {
    public var model: String
    public var tokens: Int64
    public var usd: Double
    public var match: Pricing.Match
}

public struct CostSummary: Equatable, Sendable {
    public var todayUSD: Double = 0
    public var todayTokens: Int64 = 0
    public var last30USD: Double = 0
    public var last30Tokens: Int64 = 0
    /// 30 daily costs in USD, oldest first, today last.
    public var dailyUSD: [Double] = Array(repeating: 0, count: 30)
    public var topModel: String?
    /// Models that contributed tokens but have no price (and so cost 0).
    public var unpricedModels: [String] = []
    /// Per model over 30 days, highest cost first.
    public var perModel: [ModelCost] = []

    public init() {}
}

public struct ScanStats: Sendable {
    public var filesFound = 0
    public var filesInWindow = 0
    public var filesRead = 0
    public var bytesRead: Int64 = 0
    public var linesDecoded = 0
    public var recordsMerged = 0
    public var uniqueKeys = 0
    /// Keys seen in more than one file during this pass (resumed or forked sessions).
    public var crossFileDuplicateKeys = 0
    public var seconds: Double = 0
}

/// One file's incremental position. No content is stored: `offset` is the byte just after the last
/// complete line, so a partial trailing line is simply re-read on the next pass.
public struct FileState: Codable, Equatable, Sendable {
    public var size: Int64
    public var mtime: Double
    public var inode: UInt64
    public var offset: Int64
}

/// Persisted state for one logs root: token counts and ids hashes only.
public struct RootCache: Codable, Sendable {
    public var files: [String: FileState]
    public var models: [String]
    /// Packed records, see `CostScanner.pack`.
    public var records: Data
}

public struct CostCacheFile: Codable, Sendable {
    public var version: Int
    public var roots: [String: RootCache]
    public static let currentVersion = 2

    public init(version: Int, roots: [String: RootCache]) {
        self.version = version
        self.roots = roots
    }
}

// MARK: - Parsed line

struct ParsedRecord {
    var key: UInt64
    var day: Int32
    var model: String
    var counts: TokenCounts
    var incomplete: Bool
}

struct Entry {
    var day: Int32
    var model: UInt16
    var counts: TokenCounts
    var incomplete: Bool
}

private struct LogLine: Decodable {
    let type: String?
    let requestId: String?
    let request_id: String?
    let sessionId: String?
    let timestamp: String?
    let message: Message?

    struct Message: Decodable {
        let id: String?
        let model: String?
        let stop_reason: String?
        let usage: Usage?
    }

    struct Usage: Decodable {
        let input_tokens: Int64?
        let output_tokens: Int64?
        let cache_read_input_tokens: Int64?
        let cache_creation_input_tokens: Int64?
        let cache_creation: CacheCreation?
    }

    struct CacheCreation: Decodable {
        let ephemeral_5m_input_tokens: Int64?
        let ephemeral_1h_input_tokens: Int64?
    }
}

// MARK: - Scanner

/// Incremental scanner over `<root>/**/*.jsonl`. Not thread-safe: call from one queue or actor.
public final class CostScanner: @unchecked Sendable {
    public let root: URL
    public var timeZone: TimeZone
    public var maxAgeDays = 31
    public var workers = 6

    private var files: [String: FileState] = [:]
    private var models: [String] = []
    private var modelIndex: [String: UInt16] = [:]
    private var entries: [UInt64: Entry] = [:]

    public init(root: URL, timeZone: TimeZone = .current) {
        self.root = root
        self.timeZone = timeZone
    }

    // MARK: Cache

    public func restore(_ cache: RootCache) {
        files = cache.files
        models = cache.models
        modelIndex = Dictionary(uniqueKeysWithValues: models.enumerated().map { ($1, UInt16($0)) })
        entries = CostScanner.unpack(cache.records, modelCount: models.count)
    }

    public func export() -> RootCache {
        RootCache(files: files, models: models, records: CostScanner.pack(entries))
    }

    // Record layout, little-endian, 36 bytes: key u64, day i32, model u16, flags u16, 5 x u32 counts.
    static let recordSize = 36

    static func pack(_ entries: [UInt64: Entry]) -> Data {
        var data = Data(count: entries.count * recordSize)
        data.withUnsafeMutableBytes { raw in
            var o = 0
            func put<T: FixedWidthInteger>(_ v: T) {
                raw.storeBytes(of: v.littleEndian, toByteOffset: o, as: T.self)
                o += MemoryLayout<T>.size
            }
            func clamp(_ v: Int64) -> UInt32 { UInt32(min(max(v, 0), Int64(UInt32.max))) }
            for (key, e) in entries {
                put(key); put(e.day); put(e.model); put(UInt16(e.incomplete ? 1 : 0))
                put(clamp(e.counts.input)); put(clamp(e.counts.output)); put(clamp(e.counts.cacheWrite5m))
                put(clamp(e.counts.cacheWrite1h)); put(clamp(e.counts.cacheRead))
            }
        }
        return data
    }

    static func unpack(_ data: Data, modelCount: Int) -> [UInt64: Entry] {
        let n = data.count / recordSize
        var out: [UInt64: Entry] = [:]
        out.reserveCapacity(n)
        data.withUnsafeBytes { raw in
            var o = 0
            func get<T: FixedWidthInteger>(_: T.Type) -> T {
                let v = raw.loadUnaligned(fromByteOffset: o, as: T.self)
                o += MemoryLayout<T>.size
                return T(littleEndian: v)
            }
            for _ in 0..<n {
                let key = get(UInt64.self), day = get(Int32.self), model = get(UInt16.self), flags = get(UInt16.self)
                let c = TokenCounts(input: Int64(get(UInt32.self)), output: Int64(get(UInt32.self)),
                                    cacheWrite5m: Int64(get(UInt32.self)), cacheWrite1h: Int64(get(UInt32.self)),
                                    cacheRead: Int64(get(UInt32.self)))
                if Int(model) < modelCount {
                    out[key] = Entry(day: day, model: model, counts: c, incomplete: flags & 1 != 0)
                }
            }
        }
        return out
    }

    // MARK: Scan

    private struct Job {
        let path: String
        let from: Int64
        let state: FileState
    }

    private struct JobResult {
        var records: [ParsedRecord] = []
        var consumed: Int64 = 0
        var bytes: Int64 = 0
        var lines = 0
        var ok = true
    }

    /// Reads new bytes from every recently modified log and merges them. Returns counts only.
    @discardableResult
    public func scan(now: Date = Date()) -> ScanStats {
        let started = Date()
        var stats = ScanStats()
        let cutoff = now.addingTimeInterval(-Double(maxAgeDays) * 86400).timeIntervalSince1970

        var jobs: [Job] = []
        var seen = Set<String>()
        if let walker = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil,
                                                       options: [.skipsHiddenFiles, .skipsPackageDescendants]) {
            for case let url as URL in walker where url.pathExtension == "jsonl" {
                stats.filesFound += 1
                let path = url.path
                var st = stat()
                guard stat(path, &st) == 0, (st.st_mode & S_IFMT) == S_IFREG else { continue }
                let mtime = Double(st.st_mtimespec.tv_sec) + Double(st.st_mtimespec.tv_nsec) / 1e9
                guard mtime >= cutoff else { continue }
                stats.filesInWindow += 1
                seen.insert(path)
                let current = FileState(size: Int64(st.st_size), mtime: mtime, inode: UInt64(st.st_ino), offset: 0)
                if let old = files[path] {
                    if old.inode == current.inode && old.size == current.size && old.mtime == current.mtime { continue }
                    // Same file grown: read only the new bytes. Shrunk or replaced: rescan from the start
                    // (de-dup keeps the totals right for records already merged).
                    let from = (old.inode == current.inode && current.size >= old.offset) ? old.offset : 0
                    jobs.append(Job(path: path, from: from, state: current))
                } else {
                    jobs.append(Job(path: path, from: 0, state: current))
                }
            }
        }
        // Forget files that were deleted or fell out of the window.
        for key in files.keys where !seen.contains(key) { files.removeValue(forKey: key) }

        jobs.sort { $0.path < $1.path }
        let results = runJobs(jobs)

        var firstFile: [UInt64: Int] = [:]
        var dupKeys = Set<UInt64>()
        for (i, job) in jobs.enumerated() {
            let r = results[i]
            guard r.ok else { continue }
            stats.filesRead += 1
            stats.bytesRead += r.bytes
            stats.linesDecoded += r.lines
            for rec in r.records {
                if let f = firstFile[rec.key], f != i { dupKeys.insert(rec.key) } else { firstFile[rec.key] = i }
                merge(rec)
                stats.recordsMerged += 1
            }
            var st = job.state
            st.offset = job.from + r.consumed
            files[job.path] = st
        }
        prune(now: now)
        stats.crossFileDuplicateKeys = dupKeys.count
        stats.uniqueKeys = entries.count
        stats.seconds = Date().timeIntervalSince(started)
        return stats
    }

    private func runJobs(_ jobs: [Job]) -> [JobResult] {
        var results = [JobResult](repeating: JobResult(), count: jobs.count)
        guard !jobs.isEmpty else { return results }
        let lock = NSLock()
        var next = 0
        let tz = timeZone
        let group = DispatchGroup()
        let queue = DispatchQueue(label: "usagebar.scan", qos: .utility, attributes: .concurrent)
        for _ in 0..<min(workers, jobs.count) {
            queue.async(group: group) {
                while true {
                    lock.lock()
                    let i = next
                    next += 1
                    lock.unlock()
                    if i >= jobs.count { return }
                    let r = autoreleasepool {
                        CostScanner.parseFile(path: jobs[i].path, from: jobs[i].from, timeZone: tz)
                    }
                    lock.lock()
                    results[i] = r
                    lock.unlock()
                }
            }
        }
        group.wait()
        return results
    }

    /// Merge rule: never let an incomplete row replace a complete one; otherwise the larger total wins,
    /// ties go to the later line.
    func merge(_ r: ParsedRecord) {
        let mi: UInt16
        if let idx = modelIndex[r.model] {
            mi = idx
        } else {
            mi = UInt16(min(models.count, Int(UInt16.max)))
            models.append(r.model)
            modelIndex[r.model] = mi
        }
        let new = Entry(day: r.day, model: mi, counts: r.counts, incomplete: r.incomplete)
        guard let old = entries[r.key] else {
            entries[r.key] = new
            return
        }
        if !old.incomplete && r.incomplete { return }
        if old.incomplete && !r.incomplete || r.counts.total >= old.counts.total {
            entries[r.key] = new
        }
    }

    private func prune(now: Date) {
        let today = CostScanner.dayNumber(now, timeZone: timeZone)
        let oldest = today - Int32(maxAgeDays) - 1
        entries = entries.filter { $0.value.day >= oldest }
    }

    // MARK: Summary

    public func summary(now: Date = Date()) -> CostSummary {
        let today = CostScanner.dayNumber(now, timeZone: timeZone)
        let first = today - 29
        // day offset (0...29) x model index -> counts
        var buckets: [Int: [UInt16: TokenCounts]] = [:]
        for e in entries.values where e.day >= first && e.day <= today {
            buckets[Int(e.day - first), default: [:]][e.model, default: TokenCounts()] += e.counts
        }

        var s = CostSummary()
        var perModel: [UInt16: (tokens: Int64, usd: Double)] = [:]
        for (dayOffset, byModel) in buckets {
            for (mi, counts) in byModel {
                let name = Int(mi) < models.count ? models[Int(mi)] : "unknown"
                let usd = Pricing.cost(counts, model: name) ?? 0
                s.dailyUSD[dayOffset] += usd
                s.last30USD += usd
                s.last30Tokens += counts.total
                if dayOffset == 29 {
                    s.todayUSD += usd
                    s.todayTokens += counts.total
                }
                perModel[mi, default: (0, 0)].tokens += counts.total
                perModel[mi, default: (0, 0)].usd += usd
            }
        }
        s.perModel = perModel.map { mi, v in
            let name = Int(mi) < models.count ? models[Int(mi)] : "unknown"
            return ModelCost(model: name, tokens: v.tokens, usd: v.usd, match: Pricing.match(name))
        }.sorted { ($0.usd, $0.tokens) > ($1.usd, $1.tokens) }
        s.topModel = s.perModel.first(where: { $0.usd > 0 })?.model
        s.unpricedModels = s.perModel.filter { $0.match == .unpriced && $0.tokens > 0 }.map(\.model).sorted()
        return s
    }

    // MARK: Parsing

    static let usageNeedle = Array("\"usage\"".utf8)
    static let assistantNeedle = Array("\"assistant\"".utf8)

    private static func parseFile(path: String, from: Int64, timeZone: TimeZone) -> JobResult {
        var result = JobResult()
        guard let handle = FileHandle(forReadingAtPath: path) else {
            result.ok = false
            return result
        }
        defer { try? handle.close() }
        do {
            try handle.seek(toOffset: UInt64(max(0, from)))
        } catch {
            result.ok = false
            return result
        }

        let decoder = JSONDecoder()
        var pending = Data()
        var pendingStart = from          // file offset of pending[0]
        let chunkSize = 4 << 20
        while true {
            // Each chunk in its own pool: FileHandle hands back autoreleased buffers.
            let more = autoreleasepool { () -> Bool in
                guard let chunk = try? handle.read(upToCount: chunkSize), !chunk.isEmpty else { return false }
                result.bytes += Int64(chunk.count)
                pending.append(chunk)
                var consumedInBuffer = 0
                pending.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
                    guard let base = raw.baseAddress else { return }
                    let count = raw.count
                    var start = 0
                    while start < count {
                        guard let nlPtr = memchr(base + start, 0x0A, count - start) else { break }
                        let nl = base.distance(to: nlPtr)
                        let len = nl - start
                        if len > 0 {
                            let lineOffset = pendingStart + Int64(start)
                            if let rec = parseLine(base + start, len, path: path, lineOffset: lineOffset,
                                                   decoder: decoder, timeZone: timeZone) {
                                result.records.append(rec)
                            }
                            result.lines += 1
                        }
                        start = nl + 1
                    }
                    consumedInBuffer = start
                }
                if consumedInBuffer > 0 {
                    pending = pending.subdata(in: consumedInBuffer..<pending.count)
                    pendingStart += Int64(consumedInBuffer)
                }
                return true
            }
            if !more { break }
        }
        result.consumed = pendingStart - from
        return result
    }

    static func parseLine(_ ptr: UnsafeRawPointer, _ len: Int, path: String, lineOffset: Int64,
                          decoder: JSONDecoder, timeZone: TimeZone) -> ParsedRecord? {
        // Cheap pre-filters before any JSON work.
        guard usageNeedle.withUnsafeBytes({ memmem(ptr, len, $0.baseAddress, $0.count) }) != nil,
              assistantNeedle.withUnsafeBytes({ memmem(ptr, len, $0.baseAddress, $0.count) }) != nil
        else { return nil }

        let data = Data(bytes: ptr, count: len)
        guard let line = try? decoder.decode(LogLine.self, from: data),
              line.type == "assistant", let msg = line.message, let u = msg.usage
        else { return nil }

        var counts = TokenCounts()
        counts.input = u.input_tokens ?? 0
        counts.output = u.output_tokens ?? 0
        counts.cacheRead = u.cache_read_input_tokens ?? 0
        // Use the 5m/1h split when present; only fall back to the aggregate (as 5m) when it is missing.
        if let cc = u.cache_creation, cc.ephemeral_5m_input_tokens != nil || cc.ephemeral_1h_input_tokens != nil {
            counts.cacheWrite5m = cc.ephemeral_5m_input_tokens ?? 0
            counts.cacheWrite1h = cc.ephemeral_1h_input_tokens ?? 0
        } else {
            counts.cacheWrite5m = u.cache_creation_input_tokens ?? 0
        }
        guard counts.total > 0 else { return nil }

        let incomplete = msg.stop_reason == nil && counts.input > 0 && counts.output == 0
            && u.cache_read_input_tokens == nil && u.cache_creation_input_tokens == nil && u.cache_creation == nil

        let keyString: String
        let requestId = line.requestId ?? line.request_id
        if let mid = msg.id, let rid = requestId {
            keyString = mid + ":" + rid
        } else if let mid = msg.id, let sid = line.sessionId {
            keyString = sid + ":" + mid
        } else if let mid = msg.id {
            keyString = mid
        } else {
            // No id at all: unique per line, stable across rescans of the same file.
            keyString = path + "#" + String(lineOffset)
        }

        guard let ts = line.timestamp, let epoch = fastEpoch(ts) ?? ISODate.parse(ts)?.timeIntervalSince1970
        else { return nil }
        let day = dayNumber(Date(timeIntervalSince1970: epoch), timeZone: timeZone)
        return ParsedRecord(key: fnv1a(keyString), day: day, model: msg.model ?? "unknown",
                            counts: counts, incomplete: incomplete)
    }

    // MARK: Helpers

    public static func dayNumber(_ date: Date, timeZone: TimeZone) -> Int32 {
        let local = date.timeIntervalSince1970 + Double(timeZone.secondsFromGMT(for: date))
        return Int32((local / 86400).rounded(.down))
    }

    static func fnv1a(_ s: String) -> UInt64 {
        var h: UInt64 = 0xcbf29ce484222325
        for b in s.utf8 {
            h ^= UInt64(b)
            h = h &* 0x100000001b3
        }
        return h
    }

    /// Fast path for "YYYY-MM-DDTHH:MM:SS[.fff]Z"; nil for anything else.
    static func fastEpoch(_ s: String) -> Double? {
        var s = s
        return s.withUTF8 { b -> Double? in
            guard b.count >= 20, b[4] == 0x2D, b[7] == 0x2D, b[10] == 0x54, b[13] == 0x3A, b[16] == 0x3A,
                  b[b.count - 1] == 0x5A else { return nil }
            func num(_ from: Int, _ n: Int) -> Int? {
                var v = 0
                for i in from..<(from + n) {
                    let c = Int(b[i]) - 48
                    guard c >= 0 && c <= 9 else { return nil }
                    v = v * 10 + c
                }
                return v
            }
            guard let y = num(0, 4), let mo = num(5, 2), let d = num(8, 2), let h = num(11, 2), let mi = num(14, 2),
                  let sec = num(17, 2) else { return nil }
            var frac = 0.0
            if b.count > 20, b[19] == 0x2E {
                var scale = 0.1
                for i in 20..<(b.count - 1) {
                    let c = Int(b[i]) - 48
                    guard c >= 0 && c <= 9 else { return nil }
                    frac += Double(c) * scale
                    scale /= 10
                }
            } else if b.count != 20 {
                return nil
            }
            // Days from civil (Howard Hinnant).
            let yy = mo <= 2 ? y - 1 : y
            let era = (yy >= 0 ? yy : yy - 399) / 400
            let yoe = yy - era * 400
            let mp = (mo + 9) % 12
            let doy = (153 * mp + 2) / 5 + d - 1
            let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
            let days = era * 146097 + doe - 719468
            return Double(days * 86400 + h * 3600 + mi * 60 + sec) + frac
        }
    }
}
