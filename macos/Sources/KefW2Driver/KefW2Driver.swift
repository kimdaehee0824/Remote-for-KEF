import Foundation
import KefRemoteCore

/// KEF W2 platform speakers (LSX II, LSX II LT, LS50 Wireless II, LS60 Wireless).
public actor KefW2Driver: SpeakerDriver {
    enum Path {
        static let authMode = "settings:/webserver/authMode"
        static let modelName = "settings:/kef/host/modelName"
        static let releaseText = "settings:/releasetext"
        static let deviceName = "settings:/deviceName"
        static let source = "settings:/kef/play/physicalSource"
        static let volume = "player:volume"
        static let mute = "settings:/mediaPlayer/mute"
        static let speakerStatus = "settings:/kef/host/speakerStatus"
        static let volumeLimit = "settings:/kef/host/volumeLimit"
        static let maximumVolume = "settings:/kef/host/maximumVolume"
    }

    /// Seconds the speaker may hold a pollQueue request open waiting for a change.
    public static let changeHoldSeconds = 10
    static let statePaths: Set<String> = [Path.source, Path.speakerStatus]
    static let devicePaths: Set<String> = [Path.maximumVolume, Path.volumeLimit, Path.deviceName]
    static let subscriptions = [Path.source, Path.speakerStatus, Path.volume, Path.mute, KefPlayback.dataPath,
                                Path.maximumVolume, Path.volumeLimit, Path.deviceName]

    private let client: KefW2Client
    private var authMode: KefAuthMode?
    private var profile: KefModelProfile?
    private var queueId: String?

    public nonisolated var host: String { client.host }

    public init(host: String, password: String, transport: any KefTransport = URLSessionKefTransport()) throws {
        client = try KefW2Client(host: host, password: password, transport: transport)
    }

    init(client: KefW2Client) {
        self.client = client
    }

    public static func validatedHost(_ text: String) -> String? {
        KefW2Client.validatedHost(text)
    }

    /// Also used to re-read the device limit while connected, so the previous
    /// identity stays in place for concurrent reads until this one succeeds.
    /// A failure leaves the driver unusable until the next successful connect.
    public func connect() async throws -> DeviceInfo {
        do {
            return try await identify()
        } catch {
            authMode = nil
            profile = nil
            throw error
        }
    }

    private func identify() async throws -> DeviceInfo {
        let mode = try await readAuthMode()
        let secure = mode == .all
        let model = try KefJSON.string(await client.get(Path.modelName, secure: secure))
        let firmware = try KefJSON.string(await client.get(Path.releaseText, secure: secure))
        let profile = KefModelProfile.resolve(modelName: model, releaseText: firmware)
        let name = try KefJSON.string(await client.get(Path.deviceName, secure: secure))

        // Device limit is optional. The app limit still applies when it is unavailable.
        var deviceLimit: Int?
        if let enabled = try? KefJSON.bool(await client.get(Path.volumeLimit, secure: secure)), enabled,
           let maximum = try? KefJSON.int(await client.get(Path.maximumVolume, secure: secure)),
           (0...100).contains(maximum) {
            deviceLimit = maximum
        }

        authMode = mode
        self.profile = profile
        return DeviceInfo(
            name: name,
            model: profile.displayName,
            firmware: firmware,
            capabilities: profile.sourceIds.count > 1
                ? [.volume, .mute, .source, .power, .playback, .changes]
                : [.volume, .mute, .power, .playback, .changes],
            sources: profile.sources,
            deviceVolumeLimit: deviceLimit,
            verifiedModel: profile.verified
        )
    }

    /// The auth mode is read without authentication first. When that fails and
    /// a password is configured, it is read again with a signed request. An
    /// unreadable or unknown mode stops control instead of assuming `none`.
    private func readAuthMode() async throws -> KefAuthMode {
        let raw: String
        do {
            raw = try KefJSON.string(await client.get(Path.authMode, secure: false))
        } catch {
            guard !client.password.isEmpty else { throw error }
            raw = try KefJSON.string(await client.get(Path.authMode, secure: true))
        }
        guard let mode = KefAuthMode(rawValue: raw) else { throw KefError.unsupportedAuthMode(raw) }
        return mode
    }

    public func read() async throws -> SpeakerSnapshot {
        guard let authMode, let profile else { throw KefError.invalidResponse(loc("기기 확인 전", "device not identified yet", "機器の確認前です")) }
        let secure = authMode == .all
        let client = client
        async let source = client.get(Path.source, secure: secure)
        async let volume = client.get(Path.volume, secure: secure)
        async let mute = client.get(Path.mute, secure: secure)
        async let status = client.get(Path.speakerStatus, secure: secure)

        let first = try await KefJSON.string(source)
        let level = try await KefJSON.int(volume)
        let muted = try await KefJSON.bool(mute)
        let power = try await KefJSON.string(status)
        // Read the input twice; a change in between means the state is not consistent.
        let second = try KefJSON.string(await client.get(Path.source, secure: secure))
        guard first == second else { throw KefError.inconsistentRead }
        guard (0...100).contains(level) else { throw KefError.invalidResponse(loc("음량 범위 오류", "volume out of range", "音量が範囲外です")) }

        let powerState: PowerState = switch power {
        case "powerOn": .on
        case "standby": .standby
        default: .unknown
        }
        let known = profile.sources.contains { $0.id == second }
        return SpeakerSnapshot(
            power: second == "standby" ? .standby : powerState,
            sourceId: known ? second : nil,
            volume: level,
            muted: muted,
            // Woken with `powerOn`, the input setting can keep that value until the input is switched
            // (seen for minutes on LSX II LT). The speaker wakes on the input it had before standby.
            onPreviousInput: second == "powerOn"
        )
    }

    public func setVolume(_ volume: Int) async throws {
        guard (0...100).contains(volume) else { throw KefError.invalidResponse(loc("음량 범위 오류", "volume out of range", "音量が範囲外です")) }
        try await write(Path.volume, .int(volume))
    }

    public func setMute(_ muted: Bool) async throws {
        try await write(Path.mute, .bool(muted))
    }

    public func setSource(_ sourceId: String) async throws {
        guard let profile, profile.sources.contains(where: { $0.id == sourceId }) else {
            throw KefError.invalidResponse(loc("지원하지 않는 입력", "unsupported input", "対応していない入力"))
        }
        try await write(Path.source, .physicalSource(sourceId))
    }

    /// Power goes through the input setting, like the KEF Connect app: `standby`
    /// turns the speaker off and `powerOn` wakes it on the input it had before.
    public func setPower(_ on: Bool) async throws {
        try await write(Path.source, .physicalSource(on ? "powerOn" : "standby"))
    }

    public nonisolated func approachOvershoot(current: Int, target: Int) -> Int? {
        target > current ? KefVolumeCurve.overshoot(for: target) : nil
    }

    /// Streaming inputs carry track information. Physical inputs only report their own name.
    public nonisolated func sourceHasPlayback(_ sourceId: String?) -> Bool {
        sourceId == "wifi" || sourceId == "bluetooth"
    }

    public func readPlayback() async throws -> PlaybackInfo {
        guard let authMode else { throw KefError.invalidResponse(loc("기기 확인 전", "device not identified yet", "機器の確認前です")) }
        let secure = authMode == .all
        let client = client
        async let data = client.get(KefPlayback.dataPath, secure: secure)
        // The position is optional: without it the player just shows no progress.
        async let time = try? client.get(KefPlayback.timePath, secure: secure)
        return try await KefPlayback.parse(data, playTime: time)
    }

    public func sendPlayback(_ command: PlaybackCommand) async throws {
        try await write(KefPlayback.controlPath, .object(KefPlayback.controlValue(command)), role: "activate")
    }

    /// Long-polls the speaker's event queue, subscribing first if needed. Any
    /// failure drops the queue so the next call subscribes again.
    public func waitForChanges() async throws -> DeviceChanges {
        do {
            let id: String
            if let queueId {
                id = queueId
            } else {
                id = try await client.subscribe(Self.subscriptions)
                queueId = id
            }
            let reply = try await client.poll(id, hold: Self.changeHoldSeconds)
            return try KefPlayback.changes(
                reply, volumePath: Path.volume, mutePath: Path.mute,
                statePaths: Self.statePaths, devicePaths: Self.devicePaths
            )
        } catch {
            queueId = nil
            throw error
        }
    }

    private func write(_ path: String, _ value: KefJSON.Typed, role: String = "value") async throws {
        guard let authMode else { throw KefError.unsupportedAuthMode("unknown") }
        try await client.set(path, value, auth: authMode, role: role)
    }
}
