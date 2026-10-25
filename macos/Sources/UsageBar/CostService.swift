import Foundation
import UsageBarCore

/// Owns one incremental scanner per logs folder and the on-disk cache (token counts only).
/// All work runs on a private serial queue so the first full scan never touches the main thread.
final class CostService: @unchecked Sendable {
    private let queue = DispatchQueue(label: "usagebar.cost", qos: .utility)
    private var scanners: [String: CostScanner] = [:]
    private var cache: CostCacheFile?
    private let cacheURL: URL

    init(cacheURL: URL = Paths.costCache) {
        self.cacheURL = cacheURL
    }

    /// Scans each root (expanded path) and returns its summary.
    func refresh(roots: [String], now: Date = Date()) async -> [String: CostSummary] {
        await withCheckedContinuation { cont in
            queue.async {
                cont.resume(returning: self.refreshSync(roots: roots, now: now))
            }
        }
    }

    private func refreshSync(roots: [String], now: Date) -> [String: CostSummary] {
        if cache == nil { cache = loadCache() }
        var changed = false
        var out: [String: CostSummary] = [:]
        for root in roots {
            let scanner: CostScanner
            if let s = scanners[root] {
                scanner = s
            } else {
                scanner = CostScanner(root: URL(fileURLWithPath: root, isDirectory: true))
                if let saved = cache?.roots[root] { scanner.restore(saved) }
                scanners[root] = scanner
            }
            let stats = scanner.scan(now: now)
            Log.cost.info("scan: \(stats.filesRead, privacy: .public)/\(stats.filesInWindow, privacy: .public) files read, \(stats.recordsMerged, privacy: .public) records, \(String(format: "%.2f", stats.seconds), privacy: .public) s")
            if stats.filesRead > 0 || cache?.roots[root] == nil {
                cache?.roots[root] = scanner.export()
                changed = true
            }
            out[root] = scanner.summary(now: now)
        }
        if changed { saveCache() }
        return out
    }

    private func loadCache() -> CostCacheFile {
        guard let data = try? Data(contentsOf: cacheURL),
              let file = try? JSONDecoder().decode(CostCacheFile.self, from: data),
              file.version == CostCacheFile.currentVersion
        else { return CostCacheFile(version: CostCacheFile.currentVersion, roots: [:]) }
        return file
    }

    private func saveCache() {
        guard let cache else { return }
        do {
            try FileManager.default.createDirectory(at: cacheURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try JSONEncoder().encode(cache)
            try data.write(to: cacheURL, options: [.atomic])
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: cacheURL.path)
        } catch {
            Log.cost.error("could not save the cost cache: \(error.localizedDescription, privacy: .public)")
        }
    }
}
