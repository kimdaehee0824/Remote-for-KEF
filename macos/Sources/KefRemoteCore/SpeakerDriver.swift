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
    /// Reports what is playing and takes play/pause, next and previous.
    public static let playback = DriverCapabilities(rawValue: 1 << 4)
    /// Reports changes as they happen (`SpeakerDriver.waitForChanges`).
    public static let changes = DriverCapabilities(rawValue: 1 << 5)
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
    /// The device is on, or waking, on the input it had before standby but
    /// doesn't say which: a KEF W2 speaker woken with `powerOn` keeps reporting
    /// `powerOn` as its input until the input is switched. The controller names
    /// it after the last input it saw while on.
    public var onPreviousInput: Bool

    public init(power: PowerState, sourceId: String?, volume: Int, muted: Bool, onPreviousInput: Bool = false) {
        self.power = power
        self.sourceId = sourceId
        self.volume = volume
        self.muted = muted
        self.onPreviousInput = onPreviousInput
    }
}

public enum PlaybackStatus: String, Sendable, Equatable {
    case stopped
    case playing
    case paused
}

public enum PlaybackCommand: String, Sendable, Equatable, CaseIterable {
    case togglePause
    case next
    case previous
}

/// What the device is playing. Text is empty when the source doesn't say.
public struct PlaybackInfo: Sendable, Equatable {
    public static let none = PlaybackInfo()

    public var status: PlaybackStatus
    public var title: String
    public var artist: String
    public var album: String
    /// http(s) URL of the cover.
    public var artworkURL: String?
    /// Display name of the streaming service, e.g. Spotify.
    public var service: String?
    /// Track length in milliseconds, 0 when unknown (radio, physical inputs).
    public var durationMs: Int
    /// Position in milliseconds when read, -1 when unknown.
    public var positionMs: Int
    public var canTogglePause: Bool
    public var canNext: Bool
    public var canPrevious: Bool

    public init(
        status: PlaybackStatus = .stopped, title: String = "", artist: String = "", album: String = "",
        artworkURL: String? = nil, service: String? = nil, durationMs: Int = 0, positionMs: Int = -1,
        canTogglePause: Bool = false, canNext: Bool = false, canPrevious: Bool = false
    ) {
        self.status = status
        self.title = title
        self.artist = artist
        self.album = album
        self.artworkURL = artworkURL
        self.service = service
        self.durationMs = durationMs
        self.positionMs = positionMs
        self.canTogglePause = canTogglePause
        self.canNext = canNext
        self.canPrevious = canPrevious
    }

    /// A track is loaded: something to show.
    public var hasTrack: Bool { status != .stopped && !title.isEmpty }
}

/// What a change report says. Volume and mute carry the reported value so the
/// controller can skip a read when it already shows it (the echo of its own
/// write); anything else asks for a full read.
public struct DeviceChanges: Sendable, Equatable {
    public static let none = DeviceChanges()

    public var volume: Int?
    public var muted: Bool?
    /// Input, power or something without a value here changed.
    public var state: Bool
    /// Name or volume limit changed: identify the device again.
    public var device: Bool
    public var playback: Bool

    public init(volume: Int? = nil, muted: Bool? = nil, state: Bool = false, device: Bool = false, playback: Bool = false) {
        self.volume = volume
        self.muted = muted
        self.state = state
        self.device = device
        self.playback = playback
    }

    public var any: Bool { volume != nil || muted != nil || state || device || playback }

    /// Later values win; flags add up.
    public func merged(with later: DeviceChanges) -> DeviceChanges {
        DeviceChanges(
            volume: later.volume ?? volume, muted: later.muted ?? muted,
            state: state || later.state, device: device || later.device, playback: playback || later.playback
        )
    }
}

public struct UnsupportedOperation: Error, CustomStringConvertible {
    public init() {}
    public var description: String { loc("이 기기는 지원하지 않습니다", "Not supported by this device", "この機器は対応していません") }
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
    /// Whether the input has track information, e.g. streaming but not a physical input.
    func sourceHasPlayback(_ sourceId: String?) -> Bool
    /// Only with `playback`. Read-only.
    func readPlayback() async throws -> PlaybackInfo
    /// Only with `playback`.
    func sendPlayback(_ command: PlaybackCommand) async throws
    /// Only with `changes`, never two at a time. Waits until the device reports
    /// a change, or for a while and returns `.none`. Read-only. After it
    /// throws, the next call starts over (e.g. subscribes again).
    func waitForChanges() async throws -> DeviceChanges
}

extension SpeakerDriver {
    public func approachOvershoot(current: Int, target: Int) -> Int? { nil }
    public func sourceHasPlayback(_ sourceId: String?) -> Bool { true }
    public func readPlayback() async throws -> PlaybackInfo { throw UnsupportedOperation() }
    public func sendPlayback(_ command: PlaybackCommand) async throws { throw UnsupportedOperation() }
    public func waitForChanges() async throws -> DeviceChanges { throw UnsupportedOperation() }
}
