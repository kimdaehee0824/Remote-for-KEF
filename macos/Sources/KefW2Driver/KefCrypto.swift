import Foundation
#if canImport(CryptoKit)
import CommonCrypto
import CryptoKit
#else
import COpenSSL
#endif

/// `HMAC_SHA256_AES256` scheme used by KEF W2 firmware when the speaker
/// webpage password protection is on. See docs/protocol/kef-w2.md.
enum KefCrypto {
    static let username = "user"
    static let usernameBase64 = "dXNlcg=="

    /// SHA256(salt || UTF8(password)).
    static func key(salt: Data, password: String) -> Data {
        sha256(salt + Data(password.utf8))
    }

    /// Base64(IV || AES-256-CBC-PKCS7(json)).
    static func encrypt(_ json: String, key: Data, iv: Data) throws -> String {
        let cipher = try aes256CBC(encrypt: true, Data(json.utf8), key: key, iv: iv)
        return (iv + cipher).base64EncodedString()
    }

    /// Inverse of `encrypt`, used by tests and fake devices.
    static func decrypt(_ base64: String, key: Data) throws -> String {
        guard let data = Data(base64Encoded: base64), data.count > 16 else { throw KefError.crypto }
        let plain = try aes256CBC(encrypt: false, data.dropFirst(16), key: key, iv: data.prefix(16))
        guard let text = String(data: plain, encoding: .utf8) else { throw KefError.crypto }
        return text
    }

    /// Signs `user.<salt>.<timestamp>.<url>[.<body>]`; GET omits the body part.
    static func authorization(url: String, body: String?, salt: Data, key: Data, timestamp: Int64) -> String {
        let saltBase64 = salt.base64EncodedString()
        var message = "\(username).\(saltBase64).\(timestamp).\(url)"
        if let body { message += ".\(body)" }
        let signature = hmacSHA256(key: key, Data(message.utf8)).base64EncodedString()
        return "HMAC_SHA256_AES256 \(usernameBase64).\(saltBase64).\(timestamp).\(signature)"
    }

    static func randomBytes(_ count: Int) -> Data {
        // SystemRandomNumberGenerator is cryptographically secure on Apple platforms and Linux.
        var generator = SystemRandomNumberGenerator()
        return Data((0..<count).map { _ in UInt8.random(in: .min ... .max, using: &generator) })
    }

    // MARK: Platform primitives

    #if canImport(CryptoKit)
    static func sha256(_ data: Data) -> Data {
        Data(SHA256.hash(data: data))
    }

    static func hmacSHA256(key: Data, _ data: Data) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: data, using: SymmetricKey(data: key)))
    }

    static func aes256CBC(encrypt: Bool, _ input: Data, key: Data, iv: Data) throws -> Data {
        guard key.count == kCCKeySizeAES256, iv.count == kCCBlockSizeAES128 else { throw KefError.crypto }
        var output = Data(count: input.count + kCCBlockSizeAES128)
        let capacity = output.count
        var written = 0
        let status = output.withUnsafeMutableBytes { out in
            input.withUnsafeBytes { inBytes in
                key.withUnsafeBytes { keyBytes in
                    iv.withUnsafeBytes { ivBytes in
                        CCCrypt(
                            CCOperation(encrypt ? kCCEncrypt : kCCDecrypt),
                            CCAlgorithm(kCCAlgorithmAES),
                            CCOptions(kCCOptionPKCS7Padding),
                            keyBytes.baseAddress, key.count,
                            ivBytes.baseAddress,
                            inBytes.baseAddress, input.count,
                            out.baseAddress, capacity,
                            &written
                        )
                    }
                }
            }
        }
        guard status == CCCryptorStatus(kCCSuccess) else { throw KefError.crypto }
        return output.prefix(written)
    }
    #else
    static func sha256(_ data: Data) -> Data {
        var digest = [UInt8](repeating: 0, count: Int(SHA256_DIGEST_LENGTH))
        let bytes = [UInt8](data)
        _ = SHA256(bytes, bytes.count, &digest)
        return Data(digest)
    }

    static func hmacSHA256(key: Data, _ data: Data) -> Data {
        var digest = [UInt8](repeating: 0, count: Int(EVP_MAX_MD_SIZE))
        var length: UInt32 = 0
        let keyBytes = [UInt8](key)
        let bytes = [UInt8](data)
        _ = HMAC(EVP_sha256(), keyBytes, Int32(keyBytes.count), bytes, bytes.count, &digest, &length)
        return Data(digest.prefix(Int(length)))
    }

    static func aes256CBC(encrypt: Bool, _ input: Data, key: Data, iv: Data) throws -> Data {
        guard key.count == 32, iv.count == 16, let context = EVP_CIPHER_CTX_new() else { throw KefError.crypto }
        defer { EVP_CIPHER_CTX_free(context) }
        let keyBytes = [UInt8](key)
        let ivBytes = [UInt8](iv)
        let bytes = [UInt8](input)
        var output = [UInt8](repeating: 0, count: bytes.count + 16)
        var written: Int32 = 0
        var final: Int32 = 0
        guard EVP_CipherInit_ex(context, EVP_aes_256_cbc(), nil, keyBytes, ivBytes, encrypt ? 1 : 0) == 1,
              EVP_CipherUpdate(context, &output, &written, bytes, Int32(bytes.count)) == 1,
              output.withUnsafeMutableBufferPointer({ EVP_CipherFinal_ex(context, $0.baseAddress! + Int(written), &final) }) == 1
        else { throw KefError.crypto }
        return Data(output.prefix(Int(written + final)))
    }
    #endif
}
