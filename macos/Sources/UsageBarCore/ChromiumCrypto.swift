import CommonCrypto
import CryptoKit
import Foundation

/// Chromium / Electron `safeStorage` on macOS ("v10"): PBKDF2-HMAC-SHA1(password, "saltysalt", 1003) -> 16-byte
/// key, AES-128-CBC with an IV of 16 spaces and PKCS7 padding. Matches Chromium's os_crypt_mac.
public enum ChromiumCrypto {
    public enum CryptoError: Error, Equatable {
        case notBase64
        case missingV10Prefix
        case keyDerivationFailed
        case decryptFailed(Int32)
    }

    static let salt = Array("saltysalt".utf8)
    static let iterations: UInt32 = 1003
    static let iv = [UInt8](repeating: 0x20, count: kCCBlockSizeAES128)
    static let prefix = Array("v10".utf8)

    public static func deriveKey(password: Data) throws -> Data {
        var key = [UInt8](repeating: 0, count: kCCKeySizeAES128)
        let status = password.withUnsafeBytes { pw -> Int32 in
            CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2),
                                 pw.baseAddress?.assumingMemoryBound(to: Int8.self), pw.count,
                                 salt, salt.count,
                                 CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA1), iterations,
                                 &key, key.count)
        }
        guard status == kCCSuccess else { throw CryptoError.keyDerivationFailed }
        return Data(key)
    }

    /// Decrypts a raw "v10…" blob.
    public static func decrypt(_ blob: Data, key: Data) throws -> Data {
        guard blob.count > prefix.count, Array(blob.prefix(prefix.count)) == prefix else {
            throw CryptoError.missingV10Prefix
        }
        // Decrypt with NO padding option, then validate PKCS7 ourselves. CCCrypt's own PKCS7 check accepts
        // a wrong key as long as the final plaintext byte happens to be a valid pad length, so it cannot be
        // trusted to reject wrong keys — a wrong key would otherwise yield garbage sent with a request.
        let raw = try crypt(CCOperation(kCCDecrypt), Data(blob.dropFirst(prefix.count)), key: key, padding: false)
        return try stripPKCS7(raw)
    }

    /// Validates and removes PKCS7 padding; throws `decryptFailed` for anything that isn't valid padding.
    static func stripPKCS7(_ data: Data) throws -> Data {
        let block = kCCBlockSizeAES128
        guard !data.isEmpty, data.count % block == 0, let pad = data.last else {
            throw CryptoError.decryptFailed(-1)
        }
        let n = Int(pad)
        guard n >= 1, n <= block, data.count >= n else { throw CryptoError.decryptFailed(-1) }
        guard data.suffix(n).allSatisfy({ $0 == pad }) else { throw CryptoError.decryptFailed(-1) }
        return data.prefix(data.count - n)
    }

    /// Decrypts a base64 string whose decoded bytes start with "v10" (config.json values start "djEw").
    public static func decryptBase64(_ string: String, key: Data) throws -> Data {
        guard let blob = Data(base64Encoded: string.trimmingCharacters(in: .whitespacesAndNewlines)) else {
            throw CryptoError.notBase64
        }
        return try decrypt(blob, key: key)
    }

    /// Used by tests to build fixtures.
    public static func encrypt(_ plaintext: Data, key: Data) throws -> Data {
        Data(prefix) + (try crypt(CCOperation(kCCEncrypt), plaintext, key: key, padding: true))
    }

    /// Newer Chromium cookie values prefix the plaintext with SHA-256(host_key). Strip it when present.
    public static func stripHostDigest(_ plaintext: Data, host: String) -> Data {
        let digest = Data(SHA256.hash(data: Data(host.utf8)))
        if plaintext.count >= digest.count, plaintext.prefix(digest.count) == digest {
            return Data(plaintext.dropFirst(digest.count))
        }
        return plaintext
    }

    private static func crypt(_ op: CCOperation, _ input: Data, key: Data, padding: Bool) throws -> Data {
        // Ciphertext (decrypt, no padding) must be block-aligned; plaintext (encrypt) is padded by CCCrypt.
        if !padding { guard !input.isEmpty, input.count % kCCBlockSizeAES128 == 0 else { throw CryptoError.decryptFailed(-1) } }
        var out = [UInt8](repeating: 0, count: input.count + kCCBlockSizeAES128)
        var outLen = 0
        let options = padding ? CCOptions(kCCOptionPKCS7Padding) : CCOptions(0)
        let status = key.withUnsafeBytes { k in
            input.withUnsafeBytes { inp in
                CCCrypt(op, CCAlgorithm(kCCAlgorithmAES), options,
                        k.baseAddress, k.count, iv,
                        inp.baseAddress, inp.count,
                        &out, out.count, &outLen)
            }
        }
        guard status == kCCSuccess else { throw CryptoError.decryptFailed(status) }
        return Data(out.prefix(outLen))
    }
}
