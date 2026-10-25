import Foundation
import Security
import SQLite3

public enum CredentialError: Error, Equatable, Sendable {
    case notFound
    case malformed
    case keychainDenied
    case keychainError(Int32)
    case noToken
    case decryptFailed
    case unsupported(String)
    case timedOut
}

// MARK: - Claude Code CLI

public struct CLIToken: Sendable {
    public var accessToken: String
    public var expiresAt: Date?
    public var subscriptionType: String?

    public func isExpired(now: Date) -> Bool {
        guard let e = expiresAt else { return false }
        return e <= now
    }
}

public enum ClaudeCodeCredentials {
    public static let defaultService = "Claude Code-credentials"

    /// Keychain service for a config dir. Only the default `~/.claude` is supported for now.
    public static func service(forConfigDir dir: String) throws -> String {
        let expanded = Paths.expand(dir).standardizedPath
        if expanded == Paths.expand("~/.claude").standardizedPath { return defaultService }
        throw CredentialError.unsupported("Only the default ~/.claude login is supported")
    }

    /// Reads the CLI's OAuth payload through `/usr/bin/security`, which is in the item's ACL and so does not prompt.
    /// Read-only: never writes or refreshes.
    public static func read(configDir: String = "~/.claude") async throws -> CLIToken {
        let service = try service(forConfigDir: configDir)
        let (status, out) = try await runSecurity(["find-generic-password", "-s", service, "-w"])
        if status == 44 { throw CredentialError.notFound }
        guard status == 0 else { throw CredentialError.keychainError(status) }
        return try parse(out)
    }

    public static func parse(_ raw: Data) throws -> CLIToken {
        var data = raw
        while let last = data.last, last == 0x0A || last == 0x0D || last == 0x20 { data.removeLast() }
        // `security -w` prints hex when the stored bytes are not printable.
        if data.first != UInt8(ascii: "{"), let s = String(data: data, encoding: .utf8), let hex = Data(hexString: s) {
            data = hex
        }
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let oauth = obj["claudeAiOauth"] as? [String: Any] else { throw CredentialError.malformed }
        guard let token = oauth["accessToken"] as? String, !token.isEmpty else { throw CredentialError.noToken }
        var expires: Date?
        if let ms = oauth["expiresAt"] as? NSNumber { expires = Date(timeIntervalSince1970: ms.doubleValue / 1000) }
        return CLIToken(accessToken: token, expiresAt: expires, subscriptionType: oauth["subscriptionType"] as? String)
    }

    public static let securityTimeout: TimeInterval = 10

    /// Runs `/usr/bin/security` with a hard timeout so a stuck call can't pin the fetch (and `inFlight`)
    /// forever. On timeout the process is killed and `.rejected` is thrown.
    static func runSecurity(_ args: [String]) async throws -> (Int32, Data) {
        try await withCheckedThrowingContinuation { cont in
            DispatchQueue.global(qos: .utility).async {
                let p = Process()
                p.executableURL = URL(fileURLWithPath: "/usr/bin/security")
                p.arguments = args
                let out = Pipe()
                p.standardOutput = out
                p.standardError = FileHandle.nullDevice
                p.standardInput = FileHandle.nullDevice
                do {
                    try p.run()
                } catch {
                    cont.resume(throwing: error)
                    return
                }
                let data = out.fileHandleForReading.readDataToEndOfFile()  // returns at EOF (process exit/kill)
                let deadline = DispatchTime.now() + securityTimeout
                let timer = DispatchWorkItem { if p.isRunning { p.terminate() } }
                DispatchQueue.global().asyncAfter(deadline: deadline, execute: timer)
                p.waitUntilExit()
                timer.cancel()
                if p.terminationReason == .uncaughtSignal {
                    cont.resume(throwing: CredentialError.timedOut)
                } else {
                    cont.resume(returning: (p.terminationStatus, data))
                }
            }
        }
    }
}

/// Non-secret account metadata Claude Code keeps in `~/.claude.json`.
public struct ClaudeCodeAccountInfo: Sendable {
    public var email: String?
    public var accountUuid: String?
    public var organizationUuid: String?
    public var organizationName: String?

    public static func read(path: String = "~/.claude.json") -> ClaudeCodeAccountInfo? {
        guard let data = FileManager.default.contents(atPath: Paths.expand(path)),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let oa = obj["oauthAccount"] as? [String: Any] else { return nil }
        return ClaudeCodeAccountInfo(email: oa["emailAddress"] as? String, accountUuid: oa["accountUuid"] as? String,
                                     organizationUuid: oa["organizationUuid"] as? String,
                                     organizationName: oa["organizationName"] as? String)
    }
}

/// Version of the installed CLI, for the User-Agent: last path component of `~/.local/bin/claude`'s link target.
public enum ClaudeCLIVersion {
    public static let fallback = "2.1.310"

    public static func current() -> String {
        let link = Paths.expand("~/.local/bin/claude")
        guard let dest = try? FileManager.default.destinationOfSymbolicLink(atPath: link) else { return fallback }
        let v = (dest as NSString).lastPathComponent
        let ok = !v.isEmpty && v.allSatisfy { $0.isNumber || $0 == "." } && v.contains(".")
        return ok ? v : fallback
    }

    public static var userAgent: String { "claude-cli/\(current()) (external, cli)" }
}

// MARK: - Claude desktop app

public struct DesktopConfig: Sendable {
    public var lastKnownAccountUuid: String?
    /// (config key, encrypted base64 value), newest format first.
    public var encryptedCaches: [(String, String)]
}

/// Reads the Claude desktop app's existing sign-in. Strictly read-only.
public final class DesktopCredentialReader: @unchecked Sendable {
    public let supportDir: URL
    private var cachedKey: Data?
    private let lock = NSLock()

    public static let safeStorageService = "Claude Safe Storage"

    public init(supportDir: URL = Paths.claudeDesktopSupport) {
        self.supportDir = supportDir
    }

    public var configURL: URL { supportDir.appendingPathComponent("config.json") }

    public func readConfig() throws -> DesktopConfig {
        guard let data = FileManager.default.contents(atPath: configURL.path) else { throw CredentialError.notFound }
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw CredentialError.malformed
        }
        var caches: [(String, String)] = []
        for key in ["oauth:tokenCacheV2", "oauth:tokenCache"] {
            if let s = obj[key] as? String, !s.isEmpty { caches.append((key, s)) }
        }
        return DesktopConfig(lastKnownAccountUuid: obj["lastKnownAccountUuid"] as? String, encryptedCaches: caches)
    }

    public var hasCachedKey: Bool {
        lock.lock(); defer { lock.unlock() }
        return cachedKey != nil
    }

    /// AES key derived from the "Claude Safe Storage" Keychain password. The first call can show a macOS
    /// Keychain prompt and blocks until it is answered, so never call it on the main thread.
    /// The derived key is kept in memory only.
    /// The real Keychain item uses account "Claude Key" (verified by attributes on this Mac), even though
    /// Electron's source implies "Claude". Try that account first, then fall back to service-only.
    public static let safeStorageAccount = "Claude Key"

    public func safeStorageKey() throws -> Data {
        lock.lock()
        if let k = cachedKey { lock.unlock(); return k }
        lock.unlock()

        func lookup(account: String?) -> OSStatus {
            var query: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: DesktopCredentialReader.safeStorageService,
                kSecReturnData as String: true,
                kSecMatchLimit as String: kSecMatchLimitOne,
            ]
            if let account { query[kSecAttrAccount as String] = account }
            var item: CFTypeRef?
            lastItem = nil
            let status = SecItemCopyMatching(query as CFDictionary, &item)
            if status == errSecSuccess { lastItem = item as? Data }
            return status
        }

        var status = lookup(account: DesktopCredentialReader.safeStorageAccount)
        if status == errSecItemNotFound { status = lookup(account: nil) }

        switch status {
        case errSecSuccess:
            guard let password = lastItem else { throw CredentialError.malformed }
            let key = try ChromiumCrypto.deriveKey(password: password)
            lock.lock(); cachedKey = key; lock.unlock()
            return key
        case errSecItemNotFound:
            throw CredentialError.notFound
        case errSecUserCanceled, errSecAuthFailed, errSecInteractionNotAllowed, errSecNoAccessForItem:
            throw CredentialError.keychainDenied
        default:
            throw CredentialError.keychainError(status)
        }
    }

    private var lastItem: Data?

    /// Decrypted token caches as parsed JSON, newest format first.
    public func decryptedCaches(config: DesktopConfig) throws -> [(String, Any)] {
        let key = try safeStorageKey()
        var out: [(String, Any)] = []
        for (name, value) in config.encryptedCaches {
            guard let plain = try? ChromiumCrypto.decryptBase64(value, key: key) else { continue }
            if let json = DesktopTokenParser.parseJSON(plain) {
                out.append((name, json))
            } else if plain.count > 32, let json = DesktopTokenParser.parseJSON(Data(plain.dropFirst(32))) {
                out.append((name, json))
            }
        }
        if out.isEmpty && !config.encryptedCaches.isEmpty { throw CredentialError.decryptFailed }
        return out
    }

    public func candidates(config: DesktopConfig) throws -> [TokenCandidate] {
        try decryptedCaches(config: config).flatMap { name, json in
            DesktopTokenParser.candidates(in: json).map { c in
                var c = c
                c.path = [name] + c.path
                return c
            }
        }
    }

    /// Org uuids for an account from `local-agent-mode-sessions/<accountUuid>/<orgUuid>/`.
    public func organizationUuids(accountUuid: String) -> [String] {
        let dir = supportDir.appendingPathComponent("local-agent-mode-sessions").appendingPathComponent(accountUuid)
        let names = (try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []
        return names.filter { UUID(uuidString: $0) != nil }.sorted()
    }

    /// `sessionKey` cookie for claude.ai from the app's Chromium cookie store (read-only, no copy on disk).
    public func sessionKeyCookie() throws -> String? {
        let path = supportDir.appendingPathComponent("Cookies").path
        guard FileManager.default.fileExists(atPath: path) else { return nil }
        var db: OpaquePointer?
        let uri = "file:" + (path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path) + "?mode=ro&immutable=1"
        guard sqlite3_open_v2(uri, &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK else {
            sqlite3_close(db)
            return nil
        }
        defer { sqlite3_close(db) }
        let sql = "SELECT host_key, encrypted_value, value FROM cookies WHERE name = 'sessionKey' "
            + "AND host_key LIKE '%claude.ai' ORDER BY expires_utc DESC LIMIT 1"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return nil }
        defer { sqlite3_finalize(stmt) }
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
        let host = sqlite3_column_text(stmt, 0).map { String(cString: $0) } ?? ""
        if let plainText = sqlite3_column_text(stmt, 2).map({ String(cString: $0) }), !plainText.isEmpty {
            return plainText
        }
        let n = Int(sqlite3_column_bytes(stmt, 1))
        guard n > 0, let blobPtr = sqlite3_column_blob(stmt, 1) else { return nil }
        let blob = Data(bytes: blobPtr, count: n)
        let key = try safeStorageKey()
        guard let plain = try? ChromiumCrypto.decrypt(blob, key: key) else { throw CredentialError.decryptFailed }
        let stripped = ChromiumCrypto.stripHostDigest(plain, host: host)
        return String(data: stripped, encoding: .utf8)
    }
}

extension String {
    var standardizedPath: String { (self as NSString).standardizingPath }
}

extension Data {
    init?(hexString: String) {
        let chars = Array(hexString.utf8)
        guard chars.count % 2 == 0, !chars.isEmpty else { return nil }
        var bytes = [UInt8]()
        bytes.reserveCapacity(chars.count / 2)
        var i = 0
        while i < chars.count {
            guard let hi = Data.hexValue(chars[i]), let lo = Data.hexValue(chars[i + 1]) else { return nil }
            bytes.append(hi << 4 | lo)
            i += 2
        }
        self.init(bytes)
    }

    private static func hexValue(_ c: UInt8) -> UInt8? {
        switch c {
        case 48...57: return c - 48
        case 65...70: return c - 55
        case 97...102: return c - 87
        default: return nil
        }
    }
}
