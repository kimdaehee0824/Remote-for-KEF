// Driver contract shared with the Windows implementation (docs/drivers.md).
// A driver talks to one physical player. SpeakerController owns every safety
// decision, so drivers only translate reads and writes.

public enum PowerState: String, Sendable, Equatable {
    case on
    case standby
    case unknown
}

public struct DriverCapabilities: OptionSet, Sendable, Hashable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }

    public static let volume = DriverCapabilities(rawValue: 1 << 0)
    public static let mute = DriverCapabilities(rawValue: 1 << 1)
    public static let source = DriverCapabilities(rawValue: 1 << 2)
    public static let power = DriverCapabilities(rawValue: 1 << 3)
}

public struct SourceOption: Identifiable, Sendable, Hashable {
    /// Driver-specific value written to the device, e.g. "usb".
    public let id: String
    public let name: String

    public init(id: String, name: String) {
        self.id = id
        self.name = name
    }
}

public struct DeviceInfo: Sendable, Equatable {
    public var name: String
    public var model: String
    public var firmware: String
    public var capabilities: DriverCapabilities
    public var sources: [SourceOption]
    /// Device-side maximum volume when the device limit is enabled.
    public var deviceVolumeLimit: Int?
    /// True only for models whose writes were tested on real hardware.
    public var verifiedModel: Bool
    /// Read-back tolerance when a native scale is mapped onto 0-100.
    public var volumeTolerance: Int

    public init(
        name: String,
        model: String,
        firmware: String,
        capabilities: DriverCapabilities,
        sources: [SourceOption],
        deviceVolumeLimit: Int?,
        verifiedModel: Bool,
        volumeTolerance: Int = 0
    ) {
        self.name = name
        self.model = model
        self.firmware = firmware
        self.capabilities = capabilities
        self.sources = sources
        self.deviceVolumeLimit = deviceVolumeLimit
        self.verifiedModel = verifiedModel
        self.volumeTolerance = volumeTolerance
    }
}

public struct SpeakerSnapshot: Sendable, Equatable {
    public var power: PowerState
    /// nil while the device reports no playable input (e.g. standby).
    public var sourceId: String?
    public var volume: Int
    public var muted: Bool

    public init(power: PowerState, sourceId: String?, volume: Int, muted: Bool) {
        self.power = power
        self.sourceId = sourceId
        self.volume = volume
        self.muted = muted
    }
}

public protocol SpeakerDriver: Sendable {
    /// Identifies the device. Read-only; called on every connect and reconnect.
    func connect() async throws -> DeviceInfo
    /// Read-only. Must return a consistent snapshot or throw.
    func read() async throws -> SpeakerSnapshot
    func setVolume(_ volume: Int) async throws
    func setMute(_ muted: Bool) async throws
    func setSource(_ sourceId: String) async throws
    /// Only with `power`. `true` wakes the device on the input it had before standby.
    func setPower(_ on: Bool) async throws
    /// A slightly higher value to pass through on the way up to `target`, for
    /// devices whose level depends on the direction it was reached from. nil
    /// writes `target` directly. The controller caps it at the volume limit.
    func approachOvershoot(current: Int, target: Int) -> Int?
}

extension SpeakerDriver {
    public func approachOvershoot(current: Int, target: Int) -> Int? { nil }
}
