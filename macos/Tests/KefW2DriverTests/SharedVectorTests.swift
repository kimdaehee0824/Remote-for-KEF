import Foundation
import Testing
@testable import KefW2Driver

/// Runs the language-neutral vectors in spec/test-vectors/kef-w2.json, which
/// the Windows tests read as well.
struct SharedVectorTests {
    static let url = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // KefW2DriverTests
        .deletingLastPathComponent()   // Tests
        .deletingLastPathComponent()   // macos
        .deletingLastPathComponent()   // repository root
        .appendingPathComponent("spec/test-vectors/kef-w2.json")

    static var available: Bool { FileManager.default.fileExists(atPath: url.path) }

    let root: [String: Any]

    init() throws {
        let data = try Data(contentsOf: Self.url)
        root = try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    func cases(_ name: String) -> [[String: Any]] { root[name] as? [[String: Any]] ?? [] }

    @Test(.enabled(if: available)) func keyDerivation() throws {
        for item in cases("keyDerivation") {
            let key = KefCrypto.key(salt: hex(item["saltHex"]), password: item["password"] as! String)
            #expect(hexString(key) == item["keyHex"] as! String)
        }
    }

    @Test(.enabled(if: available)) func encryption() throws {
        for item in cases("encryption") {
            let envelope = try KefCrypto.encrypt(item["plaintext"] as! String, key: hex(item["keyHex"]), iv: hex(item["ivHex"]))
            #expect(envelope == item["envelopeBase64"] as! String)
        }
    }

    @Test(.enabled(if: available)) func authorization() {
        for item in cases("authorization") {
            let header = KefCrypto.authorization(
                url: item["url"] as! String, body: item["body"] as? String,
                salt: hex(item["saltHex"]), key: hex(item["keyHex"]),
                timestamp: (item["timestampMs"] as! NSNumber).int64Value
            )
            #expect(header == item["header"] as! String)
        }
    }

    @Test(.enabled(if: available)) func secureSetData() throws {
        for item in cases("secureSetData") {
            let salt = hex(item["saltHex"])
            let key = KefCrypto.key(salt: salt, password: item["password"] as! String)
            let typed = try typedValue(item["typedValue"] as! [String: Any])
            let encrypted = try KefCrypto.encrypt(typed.json, key: key, iv: hex(item["ivHex"]))
            let body = KefJSON.encryptedBody(path: item["path"] as! String, encryptedValue: encrypted)
            #expect(body == item["body"] as! String)
            let client = try KefW2Client(host: item["host"] as! String, password: "", transport: FakeKefDevice())
            let header = KefCrypto.authorization(
                url: client.setURL, body: body, salt: salt, key: key,
                timestamp: (item["timestampMs"] as! NSNumber).int64Value
            )
            #expect(header == item["header"] as! String)
        }
    }

    @Test(.enabled(if: available)) func getDataUrl() throws {
        for item in cases("getDataUrl") {
            let client = try KefW2Client(host: item["host"] as! String, password: "", transport: FakeKefDevice())
            #expect(client.getURL(item["path"] as! String) == item["url"] as! String)
        }
    }

    @Test(.enabled(if: available)) func responseParsing() throws {
        for item in cases("responseParsing") {
            let text = item["json"] as! String
            let shouldFail = item["error"] as? Bool ?? false
            let value = try? KefJSON.decode(Data(text.utf8))
            switch item["kind"] as! String {
            case "int":
                let parsed = value.flatMap { try? KefJSON.int($0) }
                #expect(parsed == (shouldFail ? nil : (item["expect"] as! NSNumber).intValue), "\(text)")
            case "bool":
                let parsed = value.flatMap { try? KefJSON.bool($0) }
                #expect(parsed == (shouldFail ? nil : item["expect"] as? Bool), "\(text)")
            default:
                let parsed = value.flatMap { try? KefJSON.string($0) }
                #expect(parsed == (shouldFail ? nil : item["expect"] as? String), "\(text)")
            }
        }
    }

    private func typedValue(_ object: [String: Any]) throws -> KefJSON.Typed {
        switch object["type"] as! String {
        case "i32_": .int((object["i32_"] as! NSNumber).intValue)
        case "bool_": .bool(object["bool_"] as! Bool)
        case "kefPhysicalSource": .physicalSource(object["kefPhysicalSource"] as! String)
        default: throw KefError.invalidResponse("unknown vector type")
        }
    }
}

private func hex(_ value: Any?) -> Data {
    let text = value as! String
    var data = Data()
    var index = text.startIndex
    while index < text.endIndex {
        let next = text.index(index, offsetBy: 2)
        data.append(UInt8(text[index..<next], radix: 16)!)
        index = next
    }
    return data
}

private func hexString(_ data: Data) -> String {
    data.map { String(format: "%02x", $0) }.joined()
}
