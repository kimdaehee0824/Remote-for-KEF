import AppKit
import Carbon.HIToolbox
import KefRemoteCore

/// What a global shortcut does. Same actions as the Windows hotkeys.
enum ShortcutAction: String, CaseIterable, Codable, Sendable {
    case volumeUp
    case volumeDown
    case toggleMute

    var name: String {
        switch self {
        case .volumeUp: loc("음량 올리기", "Volume up", "音量を上げる")
        case .volumeDown: loc("음량 내리기", "Volume down", "音量を下げる")
        case .toggleMute: loc("음소거 켜기/끄기", "Mute on/off", "ミュートのオン/オフ")
        }
    }

    var symbol: String {
        switch self {
        case .volumeUp: "speaker.plus"
        case .volumeDown: "speaker.minus"
        case .toggleMute: "speaker.slash"
        }
    }

    /// Volume keys repeat while held, like the system's; mute does not.
    var repeats: Bool { self != .toggleMute }
}

/// A key with its modifiers, as saved in the defaults.
struct Shortcut: Codable, Equatable, Sendable {
    /// Virtual key code (kVK_*).
    var keyCode: UInt16
    /// `NSEvent.ModifierFlags` raw value: control, option, shift and command only.
    var modifiers: UInt
    /// How the key is shown, e.g. "↑" or "M".
    var key: String

    static let relevantModifiers: NSEvent.ModifierFlags = [.control, .option, .shift, .command]

    init(keyCode: UInt16, modifiers: NSEvent.ModifierFlags, key: String) {
        self.keyCode = keyCode
        self.modifiers = modifiers.intersection(Self.relevantModifiers).rawValue
        self.key = key
    }

    /// ⌃⌥⌘ with the arrows and M. ⌃⌥ alone is taken by window managers such as
    /// Rectangle and Magnet, ⌃↑/↓ by Mission Control and ⌥↑/↓ by text editing.
    static let defaults: [ShortcutAction: Shortcut] = [
        .volumeUp: Shortcut(keyCode: UInt16(kVK_UpArrow), modifiers: [.control, .option, .command], key: "↑"),
        .volumeDown: Shortcut(keyCode: UInt16(kVK_DownArrow), modifiers: [.control, .option, .command], key: "↓"),
        .toggleMute: Shortcut(keyCode: UInt16(kVK_ANSI_M), modifiers: [.control, .option, .command], key: "M"),
    ]

    var flags: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifiers) }

    /// The same key and modifiers, whatever the label.
    func sameKeys(as other: Shortcut) -> Bool { keyCode == other.keyCode && modifiers == other.modifiers }

    /// In the order the system menus use: ⌃⌥⇧⌘.
    var display: String {
        var text = ""
        if flags.contains(.control) { text += "⌃" }
        if flags.contains(.option) { text += "⌥" }
        if flags.contains(.shift) { text += "⇧" }
        if flags.contains(.command) { text += "⌘" }
        return text + key
    }

    var carbonModifiers: UInt32 {
        var value = 0
        if flags.contains(.control) { value |= controlKey }
        if flags.contains(.option) { value |= optionKey }
        if flags.contains(.shift) { value |= shiftKey }
        if flags.contains(.command) { value |= cmdKey }
        return UInt32(value)
    }

    static let functionKeys: [Int: String] = [
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6",
        kVK_F7: "F7", kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12",
        kVK_F13: "F13", kVK_F14: "F14", kVK_F15: "F15", kVK_F16: "F16", kVK_F17: "F17",
        kVK_F18: "F18", kVK_F19: "F19", kVK_F20: "F20",
    ]

    static let namedKeys: [Int: String] = [
        kVK_UpArrow: "↑", kVK_DownArrow: "↓", kVK_LeftArrow: "←", kVK_RightArrow: "→",
        kVK_Return: "↩", kVK_Tab: "⇥", kVK_Space: "Space", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦",
        kVK_Home: "↖", kVK_End: "↘", kVK_PageUp: "⇞", kVK_PageDown: "⇟",
        kVK_ANSI_KeypadEnter: "⌤",
    ]

    /// The shortcut a key press in the recorder stands for, or why it can't be one.
    static func recorded(keyCode: UInt16, modifiers: NSEvent.ModifierFlags, characters: String?) -> Result<Shortcut, RecordingError> {
        let modifiers = modifiers.intersection(relevantModifiers)
        let code = Int(keyCode)
        let label: String
        if let name = functionKeys[code] ?? namedKeys[code] {
            label = name
        } else if let characters, let first = characters.unicodeScalars.first, !CharacterSet.controlCharacters.contains(first),
                  first.value < 0xF700 || first.value > 0xF8FF {
            label = characters.uppercased()
        } else {
            return .failure(.unsupportedKey)
        }
        // A function key may be used alone. Anything else needs ⌃, ⌥ or ⌘ so typing is left alone.
        if functionKeys[code] == nil, modifiers.isDisjoint(with: [.control, .option, .command]) {
            return .failure(.needsModifier)
        }
        return .success(Shortcut(keyCode: keyCode, modifiers: modifiers, key: label))
    }

    enum RecordingError: Error {
        case needsModifier
        case unsupportedKey
        case duplicate(ShortcutAction)

        var message: String {
            switch self {
            case .needsModifier: loc("⌃, ⌥, ⌘ 중 하나 이상과 함께 눌러 주세요", "Press it with ⌃, ⌥ or ⌘", "⌃、⌥、⌘のいずれかと一緒に押してください")
            case .unsupportedKey: loc("이 키는 단축키로 쓸 수 없습니다", "This key can't be used as a shortcut", "このキーはショートカットに使えません")
            case let .duplicate(other): loc("'\(other.name)'와 같은 조합입니다", "Same combination as '\(other.name)'", "「\(other.name)」と同じ組み合わせです")
            }
        }
    }
}

/// Registers global shortcuts with `RegisterEventHotKey`. Unlike an event tap this
/// needs no Accessibility or Input Monitoring permission. A held volume key repeats
/// at the system's key repeat rate until it or its modifiers are let go.
@MainActor
final class HotkeyCenter {
    static let shared = HotkeyCenter()

    /// Called on the main thread for each press and each repeat.
    var onPress: ((ShortcutAction) -> Void)?

    nonisolated private static let signature: OSType = 0x4B45_4652 // "KEFR"
    private var handler: EventHandlerRef?
    private var registered: [ShortcutAction: (shortcut: Shortcut, ref: EventHotKeyRef)] = [:]
    private var repeating: [ShortcutAction: Task<Void, Never>] = [:]

    /// Replaces every registration. Returns the actions whose shortcut couldn't be
    /// registered, usually because another app holds the same combination.
    @discardableResult
    func register(_ shortcuts: [ShortcutAction: Shortcut]) -> Set<ShortcutAction> {
        installHandler()
        unregisterAll()
        var failed = Set<ShortcutAction>()
        for action in ShortcutAction.allCases {
            guard let shortcut = shortcuts[action] else { continue }
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: Self.signature, id: Self.number(action))
            let status = RegisterEventHotKey(UInt32(shortcut.keyCode), shortcut.carbonModifiers, id, GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref {
                registered[action] = (shortcut, ref)
            } else {
                failed.insert(action)
            }
        }
        return failed
    }

    func unregisterAll() {
        for task in repeating.values { task.cancel() }
        repeating = [:]
        for (_, entry) in registered { UnregisterEventHotKey(entry.ref) }
        registered = [:]
    }

    private static func number(_ action: ShortcutAction) -> UInt32 {
        UInt32(ShortcutAction.allCases.firstIndex(of: action) ?? 0) + 1
    }

    private func installHandler() {
        guard handler == nil else { return }
        var types = [
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
            EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased)),
        ]
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            guard let event else { return OSStatus(eventNotHandledErr) }
            var id = EventHotKeyID()
            let status = GetEventParameter(
                event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                nil, MemoryLayout<EventHotKeyID>.size, nil, &id
            )
            guard status == noErr, id.signature == HotkeyCenter.signature else { return OSStatus(eventNotHandledErr) }
            let pressed = GetEventKind(event) == UInt32(kEventHotKeyPressed)
            let number = id.id
            // The application event target is served on the main thread.
            MainActor.assumeIsolated { HotkeyCenter.shared.handle(number, pressed: pressed) }
            return noErr
        }, types.count, &types, nil, &handler)
    }

    private func handle(_ number: UInt32, pressed: Bool) {
        guard let action = ShortcutAction.allCases.first(where: { Self.number($0) == number }),
              let shortcut = registered[action]?.shortcut else { return }
        guard pressed else {
            repeating.removeValue(forKey: action)?.cancel()
            return
        }
        // A press while repeating is the system's own key repeat; the timer covers it.
        guard repeating[action] == nil else { return }
        onPress?(action)
        guard action.repeats else { return }
        repeating[action] = Task { [weak self] in
            var wait = NSEvent.keyRepeatDelay
            // At most a full sweep of the range, in case the release never arrives.
            for _ in 0..<100 {
                try? await Task.sleep(for: .seconds(wait))
                guard !Task.isCancelled, let self else { return }
                // Let go of the modifiers ends the repeat even without a release event.
                guard NSEvent.modifierFlags.intersection(Shortcut.relevantModifiers).isSuperset(of: shortcut.flags) else { break }
                self.onPress?(action)
                wait = NSEvent.keyRepeatInterval
            }
            self?.repeating[action] = nil
        }
    }
}
