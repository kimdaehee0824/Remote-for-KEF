import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public enum KefError: Error, Equatable, CustomStringConvertible {
    case invalidHost
    case unauthorized
    case loginPage
    case http(Int)
    case invalidResponse(String)
    case rejected
    case unsupportedAuthMode(String)
    case inconsistentRead
    case crypto
    case network(String)

    public var description: String {
        switch self {
        case .invalidHost: "사설 네트워크의 IPv4 주소를 입력하세요"
        case .unauthorized: "인증 거부: 스피커 웹페이지 암호와 Mac 시간을 확인하세요"
        case .loginPage: "JSON 대신 로그인 페이지가 반환되었습니다"
        case let .http(code): "KEF HTTP \(code)"
        case let .invalidResponse(detail): "응답 형식 오류: \(detail)"
        case .rejected: "KEF API가 명령을 거부했습니다"
        case let .unsupportedAuthMode(mode): "지원하지 않는 인증 방식: \(mode)"
        case .inconsistentRead: "입력 변경 중: 다음 상태 확인까지 제어 중지"
        case .crypto: "암호화 처리 실패"
        case let .network(detail): detail
        }
    }
}

public enum KefAuthMode: String, Sendable {
    case none
    case setData
    case all
}

/// Abstracts HTTP so the protocol can be tested against a fake device.
public protocol KefTransport: Sendable {
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

/// Talks to the local `/api/getData` and `/api/setData` endpoints.
struct KefW2Client: Sendable {
    let host: String
    let password: String
    let transport: any KefTransport
    let now: @Sendable () -> Date

    static let maxResponseBytes = 262_144

    init(host: String, password: String, transport: any KefTransport, now: @escaping @Sendable () -> Date = { Date() }) throws {
        guard let normalized = Self.validatedHost(host) else { throw KefError.invalidHost }
        self.host = normalized
        self.password = password
        self.transport = transport
        self.now = now
    }

    /// Accepts private, loopback and link-local IPv4 only, matching Windows.
    static func validatedHost(_ text: String) -> String? {
        let parts = text.trimmingCharacters(in: .whitespaces).split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var bytes: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.count <= 3, part.allSatisfy(\.isASCII), part.allSatisfy(\.isNumber),
                  let value = Int(part), value <= 255, part == "0" || !part.hasPrefix("0")
            else { return nil }
            bytes.append(value)
        }
        let isPrivate = bytes[0] == 10
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 172 && (16...31).contains(bytes[1]))
            || bytes[0] == 127
            || (bytes[0] == 169 && bytes[1] == 254)
        return isPrivate ? bytes.map(String.init).joined(separator: ".") : nil
    }

    static let pathAllowed: CharacterSet = {
        var set = CharacterSet()
        set.insert(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        return set
    }()

    func getURL(_ path: String) -> String {
        let escaped = path.addingPercentEncoding(withAllowedCharacters: Self.pathAllowed) ?? path
        return "http://\(host)/api/getData?path=\(escaped)&roles=value"
    }

    var setURL: String { "http://\(host)/api/setData" }

    func get(_ path: String, secure: Bool) async throws -> JSONValue {
        let url = getURL(path)
        var authorization: String?
        if secure {
            let salt = KefCrypto.randomBytes(6)
            let key = KefCrypto.key(salt: salt, password: password)
            authorization = KefCrypto.authorization(url: url, body: nil, salt: salt, key: key, timestamp: timestamp())
        }
        guard let json = try await send(method: "GET", url: url, body: nil, authorization: authorization) else {
            throw KefError.invalidResponse("빈 응답")
        }
        return json
    }

    func set(_ path: String, _ value: KefJSON.Typed, auth: KefAuthMode) async throws {
        let body: String
        var authorization: String?
        switch auth {
        case .none:
            body = KefJSON.plainBody(path: path, value: value)
        case .setData, .all:
            let salt = KefCrypto.randomBytes(6)
            let key = KefCrypto.key(salt: salt, password: password)
            let encrypted = try KefCrypto.encrypt(value.json, key: key, iv: KefCrypto.randomBytes(16))
            body = KefJSON.encryptedBody(path: path, encryptedValue: encrypted)
            authorization = KefCrypto.authorization(url: setURL, body: body, salt: salt, key: key, timestamp: timestamp())
        }
        _ = try await send(method: "POST", url: setURL, body: body, authorization: authorization)
    }

    private func timestamp() -> Int64 {
        Int64((now().timeIntervalSince1970 * 1000).rounded(.down))
    }

    private func send(method: String, url: String, body: String?, authorization: String?) async throws -> JSONValue? {
        guard let target = URL(string: url) else { throw KefError.invalidHost }
        var request = URLRequest(url: target)
        request.httpMethod = method
        request.timeoutInterval = 1.4
        if let body {
            request.httpBody = Data(body.utf8)
            request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type")
        }
        if let authorization { request.setValue(authorization, forHTTPHeaderField: "Authorization") }

        let (data, response): (Data, HTTPURLResponse)
        do {
            (data, response) = try await transport.send(request)
        } catch let error as KefError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw KefError.network(Self.describeNetwork(error))
        }

        if response.statusCode == 401 || response.statusCode == 403 { throw KefError.unauthorized }
        guard (200..<300).contains(response.statusCode) else { throw KefError.http(response.statusCode) }
        guard data.count <= Self.maxResponseBytes else { throw KefError.invalidResponse("응답이 너무 큽니다") }
        let text = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("<") { throw KefError.loginPage }
        if text.isEmpty {
            if method == "GET" { throw KefError.invalidResponse("빈 응답") }
            return nil
        }
        let json = try KefJSON.decode(Data(text.utf8))
        if case let .object(fields) = json, let error = fields["error"], error != .null, error != .bool(false) {
            throw KefError.rejected
        }
        return json
    }

    static func describeNetwork(_ error: any Error) -> String {
        guard let error = error as? URLError else { return "네트워크 오류" }
        switch error.code {
        case .timedOut: return "응답 시간 초과: 스피커 전원과 IP를 확인하세요"
        case .cannotConnectToHost, .networkConnectionLost, .notConnectedToInternet, .cannotFindHost:
            return "스피커에 연결할 수 없습니다. 같은 네트워크인지, macOS 로컬 네트워크 권한이 허용됐는지 확인하세요"
        default: return "네트워크 오류 (\(error.code.rawValue))"
        }
    }
}

/// URLSession transport: no proxy, no redirects, no cache or cookies.
public final class URLSessionKefTransport: NSObject, KefTransport, URLSessionTaskDelegate, @unchecked Sendable {
    private let session: URLSession

    public override init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 1.4
        configuration.timeoutIntervalForResource = 3
        configuration.connectionProxyDictionary = [:]
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.httpMaximumConnectionsPerHost = 2
        session = URLSession(configuration: configuration)
        super.init()
    }

    deinit { session.invalidateAndCancel() }

    public func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request, delegate: self)
        guard let http = response as? HTTPURLResponse else { throw KefError.invalidResponse("HTTP 응답이 아닙니다") }
        return (data, http)
    }

    public func urlSession(
        _ session: URLSession, task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest
    ) async -> URLRequest? {
        nil
    }
}
