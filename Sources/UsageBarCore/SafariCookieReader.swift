import Foundation

public struct SafariCookie: Equatable, Sendable {
    public var domain: String
    public var name: String
    public var path: String
    public var value: String
    public var expiry: Date?
    public var creation: Date?
    public var secure: Bool
    public var httpOnly: Bool
}

public enum SafariCookieError: Error, Equatable, Sendable {
    case fullDiskAccess
    case notFound
    case unreadable(Int)
}

/// Reads Safari's `Cookies.binarycookies` files. STRICTLY read-only; never writes Safari's files.
///
/// Format (Apple absolute/"Cocoa" epoch for dates = seconds since 2001-01-01 UTC):
///   header: "cook", page count (BE uint32), page sizes (BE uint32 each)
///   page:   0x00000100, cookie count (LE uint32), record offsets (LE uint32, from page start), footer
///   record: size (LE u32 @0), flags (LE u32 @8), url/name/path/value offsets (LE u32 @16/20/24/28,
///           from record start, NUL-terminated strings), expiry (LE double @40), creation (LE double @48)
/// Layout reference: libyal/dtformats "Safari Cookies.binarycookies". Epoch is the documented Mac
/// absolute-time convention (not stated in that page).
public enum SafariBinaryCookies {
    static let magic = Array("cook".utf8)
    static let pageMagic: [UInt8] = [0x00, 0x00, 0x01, 0x00]
    /// Seconds between the Unix epoch (1970) and the Mac absolute-time epoch (2001-01-01).
    static let macEpochOffset: TimeInterval = 978_307_200

    public static func parse(_ data: Data) -> [SafariCookie] {
        let bytes = [UInt8](data)
        let n = bytes.count
        guard n >= 8, Array(bytes[0..<4]) == magic else { return [] }
        let pageCount = Int(beUInt32(bytes, 4))
        guard pageCount >= 0, 8 + pageCount * 4 <= n else { return [] }
        var pageSizes: [Int] = []
        pageSizes.reserveCapacity(pageCount)
        for i in 0..<pageCount { pageSizes.append(Int(beUInt32(bytes, 8 + i * 4))) }

        var cookies: [SafariCookie] = []
        var pageStart = 8 + pageCount * 4
        for size in pageSizes {
            guard size >= 8, pageStart + size <= n else { break }
            cookies.append(contentsOf: parsePage(bytes, pageStart: pageStart, pageSize: size))
            pageStart += size
        }
        return cookies
    }

    private static func parsePage(_ bytes: [UInt8], pageStart: Int, pageSize: Int) -> [SafariCookie] {
        let pageEnd = pageStart + pageSize
        guard Array(bytes[pageStart..<pageStart + 4]) == pageMagic else { return [] }
        let count = Int(leUInt32(bytes, pageStart + 4))
        guard count >= 0, pageStart + 8 + count * 4 <= pageEnd else { return [] }
        var out: [SafariCookie] = []
        out.reserveCapacity(count)
        for i in 0..<count {
            let recOffset = Int(leUInt32(bytes, pageStart + 8 + i * 4))
            let recStart = pageStart + recOffset
            guard recStart + 56 <= pageEnd else { continue }
            if let c = parseRecord(bytes, recStart: recStart, pageEnd: pageEnd) { out.append(c) }
        }
        return out
    }

    private static func parseRecord(_ bytes: [UInt8], recStart: Int, pageEnd: Int) -> SafariCookie? {
        let size = Int(leUInt32(bytes, recStart))
        guard size >= 56, recStart + size <= pageEnd else { return nil }
        let flags = leUInt32(bytes, recStart + 8)
        let urlOff = Int(leUInt32(bytes, recStart + 16))
        let nameOff = Int(leUInt32(bytes, recStart + 20))
        let pathOff = Int(leUInt32(bytes, recStart + 24))
        let valueOff = Int(leUInt32(bytes, recStart + 28))
        let expiry = leDouble(bytes, recStart + 40)
        let creation = leDouble(bytes, recStart + 48)

        func str(_ off: Int) -> String? {
            guard off >= 0, recStart + off < recStart + size else { return nil }
            return cString(bytes, start: recStart + off, limit: recStart + size)
        }
        guard let domain = str(urlOff), let name = str(nameOff), let value = str(valueOff) else { return nil }
        let path = str(pathOff) ?? "/"

        func date(_ v: Double) -> Date? {
            guard v > 0, v.isFinite else { return nil }
            return Date(timeIntervalSince1970: v + macEpochOffset)
        }
        return SafariCookie(domain: domain, name: name, path: path, value: value,
                            expiry: date(expiry), creation: date(creation),
                            secure: flags & 0x1 != 0, httpOnly: flags & 0x4 != 0)
    }

    static func cString(_ bytes: [UInt8], start: Int, limit: Int) -> String? {
        var end = start
        while end < limit, bytes[end] != 0 { end += 1 }
        return String(bytes: bytes[start..<end], encoding: .utf8)
    }

    static func beUInt32(_ b: [UInt8], _ o: Int) -> UInt32 {
        UInt32(b[o]) << 24 | UInt32(b[o + 1]) << 16 | UInt32(b[o + 2]) << 8 | UInt32(b[o + 3])
    }
    static func leUInt32(_ b: [UInt8], _ o: Int) -> UInt32 {
        UInt32(b[o]) | UInt32(b[o + 1]) << 8 | UInt32(b[o + 2]) << 16 | UInt32(b[o + 3]) << 24
    }
    static func leDouble(_ b: [UInt8], _ o: Int) -> Double {
        var bits: UInt64 = 0
        for i in 0..<8 { bits |= UInt64(b[o + i]) << (8 * i) }
        return Double(bitPattern: bits)
    }
}

/// Finds Safari's cookie files and extracts the claude.ai sign-in. Read-only.
public final class SafariCookieReader: @unchecked Sendable {
    public let home: String

    public init(home: String = Paths.home) {
        self.home = home
    }

    var containerLibrary: String { "\(home)/Library/Containers/com.apple.Safari/Data/Library" }

    /// Cookie files to search, newest-modified first: the two well-known Safari files (container and
    /// legacy ~/Library/Cookies/Cookies.binarycookies), every *.binarycookies a few levels down in the
    /// Safari container, ~/Library/Cookies and ~/Library/HTTPStorages. Only claude.ai cookies are kept.
    /// `listingDenied` is true when TCC refused to list the Safari container (no Full Disk Access).
    public func candidateFiles() -> (files: [URL], listingDenied: Bool) {
        var files: [(URL, Date)] = []
        var seen = Set<String>()
        let fm = FileManager.default
        func add(_ url: URL) {
            let path = url.standardizedFileURL.path
            guard !seen.contains(path) else { return }
            seen.insert(path)
            let m = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            files.append((url, m))
        }
        // Explicit files first, so a TCC-blocked file still gets attempted and reports its permission error.
        add(URL(fileURLWithPath: "\(containerLibrary)/Cookies/Cookies.binarycookies"))
        add(URL(fileURLWithPath: "\(home)/Library/Cookies/Cookies.binarycookies"))

        var listingDenied = false
        let dirs = ["\(containerLibrary)/Cookies", "\(containerLibrary)/HTTPStorages", containerLibrary,
                    "\(home)/Library/Cookies", "\(home)/Library/HTTPStorages"]
        for dir in dirs {
            do {
                _ = try fm.contentsOfDirectory(atPath: dir)
            } catch {
                if dir.hasPrefix(containerLibrary), SafariCookieReader.isPermissionError(error) { listingDenied = true }
                continue
            }
            guard let en = fm.enumerator(at: URL(fileURLWithPath: dir, isDirectory: true),
                                         includingPropertiesForKeys: [.contentModificationDateKey],
                                         options: [.skipsHiddenFiles]) else { continue }
            var count = 0
            for case let url as URL in en {
                if en.level > 6 { en.skipDescendants(); continue }
                guard url.pathExtension == "binarycookies" else { continue }
                add(url)
                count += 1
                if count > 50 { break }
            }
        }
        return (files.sorted { $0.1 > $1.1 }.map(\.0), listingDenied)
    }

    static func isPermissionError(_ error: Error) -> Bool {
        let e = error as NSError
        if e.domain == NSCocoaErrorDomain && e.code == NSFileReadNoPermissionError { return true }
        if e.domain == NSPOSIXErrorDomain && (e.code == Int(EPERM) || e.code == Int(EACCES)) { return true }
        if let u = e.userInfo[NSUnderlyingErrorKey] as? NSError,
           u.domain == NSPOSIXErrorDomain, u.code == Int(EPERM) || u.code == Int(EACCES) { return true }
        return false
    }

    /// claude.ai cookies across Safari's stores. If no `sessionKey` turns up and any Safari store was
    /// unreadable for permission reasons, that's reported as `.fullDiskAccess` (the likely real cause),
    /// not as "no sign-in".
    public func claudeCookies() throws -> [SafariCookie] {
        let (files, listingDenied) = candidateFiles()
        var out: [SafariCookie] = []
        var denied = listingDenied
        var readAny = false
        for url in files {
            do {
                let data = try Data(contentsOf: url, options: [.mappedIfSafe])
                readAny = true
                out.append(contentsOf: SafariBinaryCookies.parse(data).filter {
                    let d = $0.domain.lowercased()
                    return d == "claude.ai" || d.hasSuffix(".claude.ai")
                })
            } catch {
                if SafariCookieReader.isPermissionError(error) { denied = true }
            }
        }
        if out.contains(where: { $0.name == "sessionKey" }) { return out }
        if denied { throw SafariCookieError.fullDiskAccess }
        if !readAny { throw SafariCookieError.notFound }
        return out
    }

    /// The claude.ai cookie jar as name→value, preferring cookies from earlier (newer) files.
    public func claudeCookieJar() throws -> [String: String] {
        var jar: [String: String] = [:]
        for c in try claudeCookies() where jar[c.name] == nil { jar[c.name] = c.value }
        return jar
    }
}
