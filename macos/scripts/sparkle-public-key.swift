// Prints the SUPublicEDKey that belongs to a Sparkle private key file (generate_keys -x):
// base64 of the 32-byte seed, or the older 96-byte form that ends with the public key.
// The release workflow compares it with Info.plist before signing an appcast.
// Usage: swift scripts/sparkle-public-key.swift <private-key-file>
import CryptoKit
import Foundation

let path = CommandLine.arguments.dropFirst().first ?? "/dev/stdin"
let text = try String(contentsOfFile: path, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
guard let key = Data(base64Encoded: text) else {
    FileHandle.standardError.write(Data("not a base64 key\n".utf8))
    exit(1)
}
switch key.count {
case 32:
    print(try Curve25519.Signing.PrivateKey(rawRepresentation: key).publicKey.rawRepresentation.base64EncodedString())
case 96:
    print(key.suffix(32).base64EncodedString())
default:
    FileHandle.standardError.write(Data("unexpected key length \(key.count)\n".utf8))
    exit(1)
}
