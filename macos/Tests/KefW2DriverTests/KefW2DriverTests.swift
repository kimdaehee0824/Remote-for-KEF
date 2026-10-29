import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
import Testing
@testable import KefRemoteCore
@testable import KefW2Driver

/// Emulates the LSX II LT HTTP API in `setData` mode: GET is plain, POST must
/// carry a valid signature and an encrypted typed value.
actor FakeKefDevice: KefTransport {
    var values: [String: String] = [
        "settings:/webserver/authMode": #"[{"type":"webserverAuthMode","webserverAuthMode":"setData"}]"#,
        "settings:/kef/host/modelName": #"[{"type":"string_","string_":"SP4077"}]"#,
        "settings:/releasetext": #"[{"type":"string_","string_":"LSXIILT_V2186"}]"#,
        "settings:/deviceName": #"[{"type":"string_","string_":"Daehee LSX II LT"}]"#,
        "settings:/kef/play/physicalSource": #"[{"type":"kefPhysicalSource","kefPhysicalSource":"usb"}]"#,
        "player:volume": #"[{"type":"i32_","i32_":32}]"#,
        "settings:/mediaPlayer/mute": #"[{"type":"bool_","bool_":false}]"#,
        "settings:/kef/host/speakerStatus": #"[{"type":"kefSpeakerStatus","kefSpeakerStatus":"powerOn"}]"#,
        "settings:/kef/host/volumeLimit": #"[{"type":"bool_","bool_":true}]"#,
        "settings:/kef/host/maximumVolume": #"[{"type":"i32_","i32_":70}]"#,
    ]
    let password: String
    var posts: [(path: String, plain: String)] = []
    var rejectedSignatures = 0
    var getURLs: [String] = []

    init(password: String = "") { self.password = password }

    func set(_ path: String, _ json: String) { values[path] = json }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let url = request.url!.absoluteString
        if request.httpMethod == "GET" {
            getURLs.append(url)
            let components = URLComponents(string: url)!
            let path = components.queryItems!.first { $0.name == "path" }!.value!
            guard let body = values[path] else { return respond(url, 404, "") }
            return respond(url, 200, body)
        }
        let body = String(decoding: request.httpBody ?? Data(), as: UTF8.self)
        guard let header = request.value(forHTTPHeaderField: "Authorization"),
              header.hasPrefix("HMAC_SHA256_AES256 ")
        else { return respond(url, 401, "") }
        let parts = header.dropFirst("HMAC_SHA256_AES256 ".count).split(separator: ".", maxSplits: 3).map(String.init)
        guard parts.count == 4, parts[0] == "dXNlcg==", let salt = Data(base64Encoded: parts[1]) else {
            return respond(url, 401, "")
        }
        let key = KefCrypto.key(salt: salt, password: password)
        let expected = KefCrypto.authorization(url: url, body: body, salt: salt, key: key, timestamp: Int64(parts[2])!)
        guard expected == header else {
            rejectedSignatures += 1
            return respond(url, 403, "")
        }
        guard case let .object(fields) = try KefJSON.decode(Data(body.utf8)),
              case let .string(path)? = fields["path"],
              case let .string(encrypted)? = fields["value"]
        else { return respond(url, 400, "") }
        let plain = try KefCrypto.decrypt(encrypted, key: key)
        posts.append((path, plain))
        values[path] = "[\(plain)]"
        return respond(url, 200, "")
    }

    private func respond(_ url: String, _ status: Int, _ body: String) -> (Data, HTTPURLResponse) {
        (Data(body.utf8), HTTPURLResponse(url: URL(string: url)!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: nil)!)
    }
}

struct KefProtocolVectorTests {
    let salt = Data([0, 1, 2, 3, 4, 5])

    @Test func keyVector() {
        let key = KefCrypto.key(salt: salt, password: "test")
        #expect(key.map { String(format: "%02x", $0) }.joined()
            == "e353821e4ed26b220f74f0a51a446e990cf3aab96d9d9fdb26651c55ad6cec42")
    }

    @Test func typedEnvelopeRoundTrips() throws {
        let key = KefCrypto.key(salt: salt, password: "test")
        let encrypted = try KefCrypto.encrypt(KefJSON.Typed.int(29).json, key: key, iv: Data(count: 16))
        #expect(try KefCrypto.decrypt(encrypted, key: key) == #"{"type":"i32_","i32_":29}"#)
        // 25 bytes of JSON pad to 32 bytes of ciphertext after the 16-byte IV.
        #expect(Data(base64Encoded: encrypted)?.count == 48)
    }

    @Test func hmacHeaderVector() {
        let key = KefCrypto.key(salt: salt, password: "test")
        let header = KefCrypto.authorization(url: "http://192.168.0.14/api/setData", body: "BODY", salt: salt, key: key, timestamp: 123)
        #expect(header == "HMAC_SHA256_AES256 dXNlcg==.AAECAwQF.123.UxPKzH1XQoHWqk6S+u0gpIahtFA8oMDIdjLPiTFPi0A=")
    }

    @Test func typedJSON() {
        #expect(KefJSON.Typed.bool(true).json == #"{"type":"bool_","bool_":true}"#)
        #expect(KefJSON.Typed.physicalSource("usb").json == #"{"type":"kefPhysicalSource","kefPhysicalSource":"usb"}"#)
        #expect(KefJSON.plainBody(path: "player:volume", value: .int(29))
            == #"{"path":"player:volume","role":"value","value":{"type":"i32_","i32_":29}}"#)
    }

    @Test func strictParsing() throws {
        #expect(try KefJSON.int(KefJSON.decode(Data(#"[{"type":"i32_","i32_":30}]"#.utf8))) == 30)
        #expect(throws: KefError.self) { try KefJSON.int(KefJSON.decode(Data(#"[{"type":"i32_","i32_":"100"}]"#.utf8))) }
        #expect(throws: KefError.self) { try KefJSON.int(KefJSON.decode(Data(#"[{"type":"bool_","bool_":true}]"#.utf8))) }
        #expect(throws: KefError.self) { try KefJSON.bool(KefJSON.decode(Data(#"[{"type":"i32_","i32_":1}]"#.utf8))) }
        #expect(throws: KefError.self) { try KefJSON.int(KefJSON.decode(Data("[]".utf8))) }
        #expect(throws: KefError.self) { try KefJSON.int(KefJSON.decode(Data(#"{"error":{"message":"x"}}"#.utf8))) }
    }

    @Test func pathIsFullyEscapedInURL() throws {
        let client = try KefW2Client(host: "192.168.0.14", password: "", transport: FakeKefDevice())
        #expect(client.getURL("player:volume") == "http://192.168.0.14/api/getData?path=player%3Avolume&roles=value")
        #expect(client.getURL("settings:/deviceName") == "http://192.168.0.14/api/getData?path=settings%3A%2FdeviceName&roles=value")
    }

    @Test func hostValidation() {
        #expect(KefW2Driver.validatedHost("192.168.0.14") == "192.168.0.14")
        #expect(KefW2Driver.validatedHost(" 10.0.0.2 ") == "10.0.0.2")
        #expect(KefW2Driver.validatedHost("172.20.1.1") == "172.20.1.1")
        #expect(KefW2Driver.validatedHost("8.8.8.8") == nil)
        #expect(KefW2Driver.validatedHost("192.168.0.014") == nil)
        #expect(KefW2Driver.validatedHost("192.168.0") == nil)
        #expect(KefW2Driver.validatedHost("kef.local") == nil)
    }

    @Test func modelProfiles() {
        let lt = KefModelProfile.resolve(modelName: "SP4077", releaseText: "LSXIILT_V2186")
        #expect(lt.id == "LSXIILT" && lt.verified)
        #expect(lt.sourceIds == ["usb", "wifi", "bluetooth", "tv", "optical"])
        #expect(KefModelProfile.resolve(modelName: "LS60W", releaseText: "").id == "LS60")
        #expect(KefModelProfile.resolve(modelName: "SP4041", releaseText: "").displayName == "LSX II")
        let luxe = KefModelProfile.resolve(modelName: "SP4042", releaseText: "")
        #expect(luxe.displayName == "LS LUXE" && !luxe.verified)
        #expect(luxe.sourceIds == ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog"])
        let unknown = KefModelProfile.resolve(modelName: "SP9999", releaseText: "NEWMODEL_V0100")
        #expect(unknown.displayName == "KEF NEWMODEL" && !unknown.verified)
        #expect(unknown.sourceIds == KefModelProfile.defaultSources)
    }
}

struct KefW2DriverTests {
    func makeDriver(_ device: FakeKefDevice, password: String = "") throws -> KefW2Driver {
        try KefW2Driver(host: "192.168.0.14", password: password, transport: device)
    }

    @Test func identifyAndReadAreGetOnly() async throws {
        let device = FakeKefDevice()
        let driver = try makeDriver(device)
        let info = try await driver.connect()
        #expect(info.name == "Daehee LSX II LT")
        #expect(info.model == "LSX II LT")
        #expect(info.verifiedModel)
        #expect(info.deviceVolumeLimit == 70)
        let state = try await driver.read()
        #expect(state == SpeakerSnapshot(power: .on, sourceId: "usb", volume: 32, muted: false))
        #expect(await device.posts.isEmpty)
    }

    @Test func signedEncryptedWrites() async throws {
        let device = FakeKefDevice()
        let driver = try makeDriver(device)
        _ = try await driver.connect()
        try await driver.setVolume(26)
        try await driver.setMute(true)
        try await driver.setSource("wifi")
        let posts = await device.posts
        #expect(posts.map(\.path) == ["player:volume", "settings:/mediaPlayer/mute", "settings:/kef/play/physicalSource"])
        #expect(posts.map(\.plain) == [
            #"{"type":"i32_","i32_":26}"#,
            #"{"type":"bool_","bool_":true}"#,
            #"{"type":"kefPhysicalSource","kefPhysicalSource":"wifi"}"#,
        ])
        let state = try await driver.read()
        #expect(state.volume == 26 && state.muted && state.sourceId == "wifi")
    }

    @Test func wrongPasswordIsRejected() async throws {
        let device = FakeKefDevice(password: "secret")
        let driver = try makeDriver(device, password: "wrong")
        _ = try await driver.connect()
        await #expect(throws: KefError.unauthorized) { try await driver.setVolume(20) }
        #expect(await device.rejectedSignatures == 1)
    }

    @Test func unknownAuthModeStopsControl() async throws {
        let device = FakeKefDevice()
        await device.set("settings:/webserver/authMode", #"[{"type":"webserverAuthMode","webserverAuthMode":"future"}]"#)
        let driver = try makeDriver(device)
        await #expect(throws: KefError.unsupportedAuthMode("future")) { try await driver.connect() }
        await #expect(throws: KefError.self) { try await driver.setVolume(20) }
        #expect(await device.posts.isEmpty)
    }

    @Test func powerGoesThroughPhysicalSource() async throws {
        let device = FakeKefDevice()
        let driver = try makeDriver(device)
        let info = try await driver.connect()
        #expect(info.capabilities.contains(.power))
        try await driver.setPower(false)
        try await driver.setPower(true)
        #expect(await device.posts.map(\.path) == ["settings:/kef/play/physicalSource", "settings:/kef/play/physicalSource"])
        #expect(await device.posts.map(\.plain) == [
            #"{"type":"kefPhysicalSource","kefPhysicalSource":"standby"}"#,
            #"{"type":"kefPhysicalSource","kefPhysicalSource":"powerOn"}"#,
        ])
    }

    @Test func overshootTable() throws {
        #expect(KefVolumeCurve.overshoot(for: 20) == 22)
        #expect(KefVolumeCurve.overshoot(for: 12) == 17)
        #expect(KefVolumeCurve.overshoot(for: 5) == nil)
        #expect(KefVolumeCurve.overshoot(for: 40) == 41)
        #expect(KefVolumeCurve.overshoot(for: 41) == nil)
        #expect(KefVolumeCurve.overshoot(for: 80) == nil)
        let driver = try makeDriver(FakeKefDevice())
        #expect(driver.approachOvershoot(current: 10, target: 20) == 22)
        #expect(driver.approachOvershoot(current: 25, target: 20) == nil)
    }

    @Test func standbyIsReported() async throws {
        let device = FakeKefDevice()
        let driver = try makeDriver(device)
        _ = try await driver.connect()
        await device.set("settings:/kef/play/physicalSource", #"[{"type":"kefPhysicalSource","kefPhysicalSource":"standby"}]"#)
        await device.set("settings:/kef/host/speakerStatus", #"[{"type":"kefSpeakerStatus","kefSpeakerStatus":"standby"}]"#)
        let state = try await driver.read()
        #expect(state.power == .standby)
        #expect(state.sourceId == nil)
    }

    /// Measured on LSX II LT: the input setting changes about 2 s before the power status when waking and about
    /// 0.4 s before it when turning off.
    @Test func inputSettingLeadsThePowerStatus() async throws {
        let device = FakeKefDevice()
        let driver = try makeDriver(device)
        _ = try await driver.connect()
        await device.set("settings:/kef/host/speakerStatus", #"[{"type":"kefSpeakerStatus","kefSpeakerStatus":"standby"}]"#)
        let waking = try await driver.read()
        #expect(waking.power == .standby)
        #expect(waking.sourceId == "usb")
        await device.set("settings:/kef/play/physicalSource", #"[{"type":"kefPhysicalSource","kefPhysicalSource":"standby"}]"#)
        await device.set("settings:/kef/host/speakerStatus", #"[{"type":"kefSpeakerStatus","kefSpeakerStatus":"powerOn"}]"#)
        let turningOff = try await driver.read()
        #expect(turningOff.power == .standby)
        #expect(turningOff.sourceId == nil)
    }

    @MainActor @Test func controllerEndToEnd() async throws {
        let device = FakeKefDevice()
        await device.set("player:volume", #"[{"type":"i32_","i32_":25}]"#)
        let driver = try makeDriver(device)
        let controller = SpeakerController(sleep: { _ in })
        await controller.connect(driver)
        #expect(await device.posts.isEmpty)
        controller.setVolume(24)
        while let task = controller.drainTask { await task.value }
        #expect(await device.posts.map(\.plain) == [#"{"type":"i32_","i32_":24}"#])
        #expect(controller.snapshot?.volume == 24)
    }
}
