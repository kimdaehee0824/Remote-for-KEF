import Foundation
import KefRemoteCore

/// Pure functions for what the speaker plays and for its event queue
/// (docs/protocol/kef-w2.md), checked against spec/test-vectors/kef-w2.json
/// like the Windows implementation.
enum KefPlayback {
    static let dataPath = "player:player/data"
    static let timePath = "player:player/data/playTime"
    static let controlPath = "player:player/control"

    /// Value for `player:player/control`, sent with the `activate` role.
    /// `pause` toggles play and pause. A bare `play` is not a resume: it ends a
    /// Spotify Connect session, so it is never sent.
    static func controlValue(_ command: PlaybackCommand) -> String {
        let control = switch command {
        case .togglePause: "pause"
        case .next: "next"
        case .previous: "previous"
        }
        return #"{"control":\#(KefJSON.quote(control))}"#
    }

    /// `player:player/data` (an array of one untyped object) and
    /// `player:player/data/playTime` (i64_ ms, -1 for none). Physical inputs
    /// report their own name as the title and no controls; callers filter
    /// those by input.
    static func parse(_ data: JSONValue, playTime: JSONValue?) throws -> PlaybackInfo {
        var root = data
        if case let .array(items) = data {
            guard items.count == 1 else { return .none }
            root = items[0]
        }
        guard case let .object(fields) = root else { return .none }
        if let error = fields["error"], error != .null, error != .bool(false) { throw KefError.rejected }

        let track = child(root, "trackRoles")
        let meta = child(child(track, "mediaData"), "metaData")
        let controls = child(root, "controls")
        var position = -1
        if let playTime, case let .int(ms)? = try? KefJSON.value(playTime), ms >= 0 { position = ms }
        var duration = 0
        if case let .int(ms)? = field(child(root, "status"), "duration"), ms > 0 { duration = ms }
        let icon = text(track, "icon")
        let status: PlaybackStatus = switch text(root, "state") {
        case "playing": .playing
        case "paused": .paused
        default: .stopped
        }

        return PlaybackInfo(
            status: status,
            title: text(track, "title") ?? "",
            artist: text(meta, "artist") ?? text(meta, "albumArtist") ?? "",
            album: text(meta, "album") ?? "",
            artworkURL: icon.flatMap { $0.hasPrefix("http://") || $0.hasPrefix("https://") ? $0 : nil },
            service: serviceName(text(meta, "serviceID")),
            durationMs: duration,
            positionMs: position,
            canTogglePause: flag(controls, "pause"),
            canNext: flag(controls, "next_") || flag(controls, "next"),
            canPrevious: flag(controls, "previous")
        )
    }

    /// Display name for a metaData.serviceID. Unknown ids are shown as sent.
    static func serviceName(_ id: String?) -> String? {
        guard let id, !id.isEmpty else { return nil }
        return switch id.lowercased() {
        case "spotify": "Spotify"
        case "airplay", "airplay2": "AirPlay"
        case "googlecast", "chromecast", "cast": "Chromecast"
        case "tidal", "tidalconnect": "TIDAL"
        case "qobuz", "qobuzconnect": "Qobuz"
        case "deezer": "Deezer"
        case "amazon", "amazonmusic": "Amazon Music"
        case "roon", "roonready": "Roon"
        case "bluetooth": "Bluetooth"
        case "airable", "radio", "internetradio", "tunein": loc("인터넷 라디오", "Internet radio", "インターネットラジオ")
        case "upnp", "dlna": "UPnP"
        default: id
        }
    }

    // MARK: Event queue

    static func modifyQueueBody(_ paths: [String]) -> String {
        let items = paths.map { #"{"path":\#(KefJSON.quote($0)),"type":"itemWithValue"}"# }
        return #"{"subscribe":[\#(items.joined(separator: ","))],"unsubscribe":[]}"#
    }

    /// Sorts a pollQueue reply, `[{"path":P,"itemType":"update","itemValue":{typed}},…]`,
    /// into what changed. The last volume and mute values win. A value that
    /// can't be read asks for a full read instead.
    static func changes(
        _ json: JSONValue, volumePath: String, mutePath: String,
        statePaths: Set<String>, devicePaths: Set<String>
    ) throws -> DeviceChanges {
        guard case let .array(items) = json else {
            throw KefError.invalidResponse(loc("이벤트 응답 형식 오류", "unexpected event reply", "イベントの応答の形式が正しくありません"))
        }
        var changes = DeviceChanges.none
        for item in items {
            guard case let .string(path)? = field(item, "path") else { continue }
            let value = field(item, "itemValue")
            if path == volumePath {
                if let value, let volume = try? KefJSON.int(value) { changes.volume = volume } else { changes.state = true }
            } else if path == mutePath {
                if let value, let muted = try? KefJSON.bool(value) { changes.muted = muted } else { changes.state = true }
            } else if statePaths.contains(path) {
                changes.state = true
            } else if devicePaths.contains(path) {
                changes.device = true
            } else if path == dataPath {
                changes.playback = true
            }
        }
        return changes
    }

    // MARK: Helpers

    private static func field(_ json: JSONValue?, _ name: String) -> JSONValue? {
        guard case let .object(fields)? = json else { return nil }
        return fields[name]
    }

    private static func child(_ json: JSONValue?, _ name: String) -> JSONValue? { field(json, name) }

    private static func text(_ json: JSONValue?, _ name: String) -> String? {
        guard case let .string(value)? = field(json, name), !value.isEmpty else { return nil }
        return value
    }

    private static func flag(_ json: JSONValue?, _ name: String) -> Bool {
        field(json, name) == .bool(true)
    }
}
