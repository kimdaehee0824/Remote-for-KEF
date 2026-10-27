import Foundation

/// Strict JSON value. Booleans and numbers are kept apart so that `true`
/// never reads as volume 1 and `"100"` never reads as a number.
enum JSONValue: Decodable, Equatable, Sendable {
    case null
    case bool(Bool)
    case int(Int)
    case double(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    init(from decoder: any Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int.self) {
            self = .int(value)
        } else if let value = try? container.decode(Double.self) {
            self = .double(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([JSONValue].self) {
            self = .array(value)
        } else {
            self = .object(try container.decode([String: JSONValue].self))
        }
    }
}

enum KefJSON {
    static func decode(_ data: Data) throws -> JSONValue {
        do {
            return try JSONDecoder().decode(JSONValue.self, from: data)
        } catch {
            throw KefError.invalidResponse("JSON이 아닌 응답")
        }
    }

    /// Unwraps `[{"type":"i32_","i32_":32}]` and `{"value": ...}` envelopes.
    static func value(_ json: JSONValue) throws -> JSONValue {
        switch json {
        case let .array(items):
            guard items.count == 1 else { throw KefError.invalidResponse("예상하지 못한 API 배열") }
            return try value(items[0])
        case let .object(fields):
            if let error = fields["error"], error != .null, error != .bool(false) {
                throw KefError.rejected
            }
            if let inner = fields["value"] { return try value(inner) }
            if case let .string(type)? = fields["type"], let inner = fields[type] { return inner }
            return json
        default:
            return json
        }
    }

    static func string(_ json: JSONValue) throws -> String {
        guard case let .string(text) = try value(json) else { throw KefError.invalidResponse("문자열 응답이 아닙니다") }
        return text
    }

    static func int(_ json: JSONValue) throws -> Int {
        guard case let .int(number) = try value(json) else { throw KefError.invalidResponse("정수 응답이 아닙니다") }
        return number
    }

    static func bool(_ json: JSONValue) throws -> Bool {
        guard case let .bool(flag) = try value(json) else { throw KefError.invalidResponse("불리언 응답이 아닙니다") }
        return flag
    }

    // Writes are built by hand so the exact bytes are known: they are
    // encrypted and signed, and must match the shared test vectors.

    enum Typed: Sendable {
        case int(Int)
        case bool(Bool)
        case physicalSource(String)

        var json: String {
            switch self {
            case let .int(n): #"{"type":"i32_","i32_":\#(n)}"#
            case let .bool(b): #"{"type":"bool_","bool_":\#(b)}"#
            case let .physicalSource(s): #"{"type":"kefPhysicalSource","kefPhysicalSource":\#(quote(s))}"#
            }
        }
    }

    static func plainBody(path: String, value: Typed) -> String {
        #"{"path":\#(quote(path)),"role":"value","value":\#(value.json)}"#
    }

    static func encryptedBody(path: String, encryptedValue: String) -> String {
        #"{"path":\#(quote(path)),"role":"value","value":\#(quote(encryptedValue))}"#
    }

    static func quote(_ text: String) -> String {
        var out = "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case _ where scalar.value < 0x20:
                out += String(format: "\\u%04x", scalar.value)
            default:
                out.unicodeScalars.append(scalar)
            }
        }
        return out + "\""
    }
}
