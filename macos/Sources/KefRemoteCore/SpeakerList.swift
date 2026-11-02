import Foundation

/// One registered speaker (a pair counts as one) and everything that belongs to it rather than to the app:
/// where it is, which keychain item holds its password, the volume limit for the room it is in, and how it is drawn.
public struct SpeakerEntry: Codable, Equatable, Identifiable, Sendable {
    /// Made by the app when the speaker is added; never changes, even when the address does.
    public var id: String
    public var host: String
    /// The name and model the speaker last reported, so the list can show it while it is off or away.
    public var name: String?
    public var model: String?
    /// Model picked in settings for a speaker that reports a model the app does not know.
    public var pickedModel: String?
    /// Finish the speaker is drawn in (`KefFinish.id`), or nil for the app's own colors.
    public var finish: String?
    /// Upper bound for raises from this app, 1...100. Lowering is always allowed.
    public var appLimit: Int
    /// Raise the volume through a short overshoot so it sounds the same as when lowered to it.
    public var approachFromAbove: Bool
    /// Last input seen. A speaker woken with the power switch doesn't say which input it came back on.
    public var lastInput: String?
    /// Keychain account of the web page password: the one of 0.4 for the speaker moved from there, else one per speaker.
    public var keychainAccount: String

    public static let legacyKeychainAccount = "speaker-webpage-password"

    public init(id: String = SpeakerEntry.newID(), host: String, name: String? = nil, model: String? = nil,
                pickedModel: String? = nil, finish: String? = nil, appLimit: Int = VolumePolicy.defaultAppLimit,
                approachFromAbove: Bool = false, lastInput: String? = nil, keychainAccount: String? = nil) {
        self.id = id
        self.host = host
        self.name = name
        self.model = model
        self.pickedModel = pickedModel
        self.finish = finish
        self.appLimit = SpeakerList.clampLimit(appLimit)
        self.approachFromAbove = approachFromAbove
        self.lastInput = lastInput
        self.keychainAccount = keychainAccount ?? "\(Self.legacyKeychainAccount)-\(id)"
    }

    /// Fields missing from what an older or newer version saved fall back to their defaults.
    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let id = try c.decodeIfPresent(String.self, forKey: .id) ?? SpeakerEntry.newID()
        self.init(
            id: id,
            host: try c.decodeIfPresent(String.self, forKey: .host) ?? "",
            name: try c.decodeIfPresent(String.self, forKey: .name),
            model: try c.decodeIfPresent(String.self, forKey: .model),
            pickedModel: try c.decodeIfPresent(String.self, forKey: .pickedModel),
            finish: try c.decodeIfPresent(String.self, forKey: .finish),
            appLimit: try c.decodeIfPresent(Int.self, forKey: .appLimit) ?? VolumePolicy.defaultAppLimit,
            approachFromAbove: try c.decodeIfPresent(Bool.self, forKey: .approachFromAbove) ?? false,
            lastInput: try c.decodeIfPresent(String.self, forKey: .lastInput),
            keychainAccount: try c.decodeIfPresent(String.self, forKey: .keychainAccount)
        )
    }

    public var isConfigured: Bool { !host.trimmingCharacters(in: .whitespaces).isEmpty }

    public static func newID() -> String {
        String(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased().prefix(12))
    }
}

/// The single speaker of 0.4 and earlier, as it was saved.
public struct LegacySpeaker: Sendable {
    public var host: String
    public var appLimit: Int?
    public var approachFromAbove: Bool
    public var finish: String?
    public var pickedModel: String?
    public var lastInput: String?

    public init(host: String, appLimit: Int? = nil, approachFromAbove: Bool = false, finish: String? = nil,
                pickedModel: String? = nil, lastInput: String? = nil) {
        self.host = host
        self.appLimit = appLimit
        self.approachFromAbove = approachFromAbove
        self.finish = finish
        self.pickedModel = pickedModel
        self.lastInput = lastInput
    }
}

/// The rules for the list of registered speakers, kept apart from the app so they can be tested.
public enum SpeakerList {
    /// The first entry made from the speaker of 0.4 and earlier, keeping its keychain item. Empty with none set up.
    public static func migrated(from legacy: LegacySpeaker?) -> [SpeakerEntry] {
        guard let legacy, !legacy.host.trimmingCharacters(in: .whitespaces).isEmpty else { return [] }
        return [SpeakerEntry(
            host: legacy.host.trimmingCharacters(in: .whitespaces),
            pickedModel: legacy.pickedModel,
            finish: legacy.finish,
            appLimit: legacy.appLimit ?? VolumePolicy.defaultAppLimit,
            approachFromAbove: legacy.approachFromAbove,
            lastInput: legacy.lastInput,
            keychainAccount: SpeakerEntry.legacyKeychainAccount
        )]
    }

    /// The speaker the panel, menu bar and shortcuts work on: the chosen one, else the first, else none.
    public static func selected(_ list: [SpeakerEntry], id: String?) -> SpeakerEntry? {
        list.first { $0.id == id } ?? list.first
    }

    /// The speaker after the selected one, wrapping around. Nil with fewer than two.
    public static func next(_ list: [SpeakerEntry], after id: String?) -> SpeakerEntry? {
        guard list.count > 1 else { return nil }
        let index = list.firstIndex { $0.id == selected(list, id: id)?.id } ?? -1
        return list[(index + 1) % list.count]
    }

    /// The list without `id`, and the selection after it: unchanged, or the one before the removed selected one.
    public static func removing(_ id: String, from list: [SpeakerEntry], selected selectedID: String?) -> (list: [SpeakerEntry], selected: String?) {
        guard let index = list.firstIndex(where: { $0.id == id }) else { return (list, selectedID) }
        let wasSelected = selected(list, id: selectedID)?.id == id
        var rest = list
        rest.remove(at: index)
        guard wasSelected else { return (rest, selectedID) }
        return (rest, rest.isEmpty ? nil : rest[max(0, index - 1)].id)
    }

    /// Another registered speaker already uses this address.
    public static func withHost(_ host: String, in list: [SpeakerEntry], except id: String? = nil) -> SpeakerEntry? {
        let wanted = host.trimmingCharacters(in: .whitespaces).lowercased()
        return list.first { $0.id != id && $0.host.trimmingCharacters(in: .whitespaces).lowercased() == wanted }
    }

    /// Name for lists: what the speaker calls itself, else its address.
    public static func displayName(_ entry: SpeakerEntry) -> String {
        if let name = entry.name, !name.isEmpty { return name }
        return entry.isConfigured ? entry.host : loc("새 스피커", "New speaker", "新しいスピーカー")
    }

    public static func clampLimit(_ value: Int) -> Int { min(100, max(1, value)) }
}
