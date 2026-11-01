import AppKit
import Carbon.HIToolbox
import KefRemoteCore
import KefW2Driver
import Observation
import ServiceManagement
import SwiftUI

enum SettingsTab: Hashable {
    case general
    case speaker
    case volume
    case shortcuts
}

/// App-level state: saved settings, the speaker controller and system hooks.
@MainActor
@Observable
final class AppModel {
    static let shared = Demo.isOn
        ? AppModel(defaults: UserDefaults(suiteName: Demo.suite) ?? .standard, usesKeychain: false)
        : AppModel()

    let controller = SpeakerController()

    private(set) var host: String
    private(set) var hasPassword: Bool
    var showsVolumeInMenuBar: Bool {
        didSet { defaults.set(showsVolumeInMenuBar, forKey: Keys.showVolume) }
    }
    /// What the speaker plays, above the volume in the panel. Off: nothing is read.
    var showsNowPlaying: Bool {
        didSet {
            controller.playbackEnabled = showsNowPlaying
            defaults.set(showsNowPlaying, forKey: Keys.showNowPlaying)
        }
    }
    var approachFromAbove: Bool {
        didSet {
            controller.approachFromAbove = approachFromAbove
            defaults.set(approachFromAbove, forKey: Keys.approachFromAbove)
        }
    }
    /// Finish the speaker icon is drawn in (KefFinish.id), or nil for the app's own look.
    var speakerFinishID: String? {
        didSet { defaults.set(speakerFinishID, forKey: Keys.speakerFinish) }
    }
    /// Model picked per speaker address, for a speaker that reports a model the app does not know.
    /// Only the drawing and the finish list use it.
    private var speakerModels: [String: String] {
        didSet { defaults.set(speakerModels, forKey: Keys.speakerModels) }
    }
    /// Last input seen per speaker address. A speaker woken with the power switch doesn't say
    /// which input it came back on, so the panel shows this one, also after the app restarts.
    @ObservationIgnored private var lastInputs: [String: String] {
        didSet { defaults.set(lastInputs, forKey: Keys.lastInputs) }
    }
    /// Global shortcuts. An action that isn't in here is off.
    private(set) var shortcuts: [ShortcutAction: Shortcut]
    /// Actions whose shortcut couldn't be registered, usually because another app holds it.
    private(set) var shortcutFailures: Set<ShortcutAction> = []
    /// The action whose shortcut settings is recording. Every shortcut is off meanwhile,
    /// so the keys reach the recorder.
    private(set) var recordingShortcut: ShortcutAction?
    private(set) var recordingError: String?
    /// Volume units per press of a volume shortcut, or `VolumeSteps.perceptual`.
    var volumeKeyStep: Int {
        didSet { defaults.set(volumeKeyStep, forKey: Keys.volumeKeyStep) }
    }
    /// Show the volume indicator when a shortcut changes the volume. Problems show either way.
    var showsVolumeHUD: Bool {
        didSet { defaults.set(showsVolumeHUD, forKey: Keys.showVolumeHUD) }
    }
    /// Tab the settings window shows when it opens next.
    var settingsTab: SettingsTab = .general
    private(set) var loginItemStatus: SMAppService.Status = .notRegistered
    private(set) var loginItemError: String?

    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let usesKeychain: Bool
    @ObservationIgnored private var started = false
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var deviceReadAt: ContinuousClock.Instant?
    @ObservationIgnored private var volumeKeys: VolumeKeys?
    @ObservationIgnored private var recordingMonitor: Any?

    private enum Keys {
        static let host = "speakerHost"
        static let appLimit = "appVolumeLimit"
        static let showVolume = "showVolumeInMenuBar"
        static let showNowPlaying = "showNowPlaying"
        static let approachFromAbove = "approachFromAbove"
        static let speakerFinish = "speakerFinish"
        static let speakerModels = "speakerModels"
        static let lastInputs = "lastInputs"
        static let shortcuts = "shortcuts"
        static let volumeKeyStep = "volumeKeyStep"
        static let showVolumeHUD = "showVolumeHUD"
    }

    /// The self-test passes its own defaults and keeps away from the keychain.
    init(defaults: UserDefaults = .standard, usesKeychain: Bool = true) {
        self.defaults = defaults
        self.usesKeychain = usesKeychain
        host = defaults.string(forKey: Keys.host) ?? ""
        hasPassword = usesKeychain && KeychainStore.password() != nil
        showsVolumeInMenuBar = defaults.object(forKey: Keys.showVolume) as? Bool ?? true
        showsNowPlaying = defaults.object(forKey: Keys.showNowPlaying) as? Bool ?? true
        approachFromAbove = defaults.bool(forKey: Keys.approachFromAbove)
        speakerFinishID = defaults.string(forKey: Keys.speakerFinish)
        speakerModels = defaults.dictionary(forKey: Keys.speakerModels) as? [String: String] ?? [:]
        lastInputs = defaults.dictionary(forKey: Keys.lastInputs) as? [String: String] ?? [:]
        shortcuts = Self.loadShortcuts(defaults.data(forKey: Keys.shortcuts))
        let step = defaults.object(forKey: Keys.volumeKeyStep) as? Int ?? 1
        volumeKeyStep = VolumeSteps.choices.contains(step) ? step : 1
        showsVolumeHUD = defaults.object(forKey: Keys.showVolumeHUD) as? Bool ?? true
        controller.approachFromAbove = approachFromAbove
        controller.playbackEnabled = showsNowPlaying
        controller.lastActiveSourceChanged = { [weak self] source in
            guard let self, !self.host.isEmpty else { return }
            self.lastInputs[self.host] = source
        }
        if defaults.object(forKey: Keys.appLimit) != nil {
            controller.appLimit = Self.clampLimit(defaults.integer(forKey: Keys.appLimit))
        }
        refreshLoginItemStatus()
    }

    func start() {
        guard !started else { return }
        started = true
        observeSleep()
        volumeKeys = VolumeKeys(model: self)
        applyShortcuts()
        controller.startMonitoring()
        if !host.isEmpty { connect() }
    }

    /// The model picked in settings for this speaker address, or nil.
    var pickedModel: String? {
        get { speakerModels[host] }
        set { speakerModels[host] = newValue }
    }

    /// The model the speaker is drawn as and whose finishes are listed (`KefModels.shown`).
    var shownModel: String? {
        KefModels.shown(reported: controller.device?.model, picked: pickedModel)
    }

    /// The picked finish, if it is one of the shown model's.
    var speakerFinish: KefFinish? {
        KefFinish.find(speakerFinishID, model: shownModel)
    }

    var appLimit: Int {
        get { controller.appLimit }
        set {
            controller.appLimit = Self.clampLimit(newValue)
            defaults.set(controller.appLimit, forKey: Keys.appLimit)
        }
    }

    private static func clampLimit(_ value: Int) -> Int { min(100, max(1, value)) }

    /// Validates and saves the address, then reconnects read-only.
    /// `password` nil keeps the stored one; an empty string removes it.
    func saveConnection(host input: String, password: String?) -> String? {
        guard let normalized = KefW2Driver.validatedHost(input) else {
            return KefError.invalidHost.description
        }
        if usesKeychain {
            do {
                if let password {
                    if password.isEmpty {
                        try KeychainStore.deletePassword()
                    } else {
                        try KeychainStore.setPassword(password)
                    }
                }
            } catch {
                return loc("암호를 키체인에 저장하지 못했습니다", "Couldn't save the password to the keychain", "パスワードをキーチェーンに保存できませんでした")
            }
            hasPassword = KeychainStore.password() != nil
        }
        host = normalized
        defaults.set(normalized, forKey: Keys.host)
        connect()
        return nil
    }

    /// Building a new driver invalidates everything queued for the old one.
    func connect() {
        let password = usesKeychain ? KeychainStore.password() ?? "" : ""
        guard !host.isEmpty, let driver = try? KefW2Driver(host: host, password: password) else {
            controller.disconnect()
            return
        }
        connect(using: driver)
    }

    func connect(using driver: any SpeakerDriver) {
        deviceReadAt = .now
        let lastInput = lastInputs[host]
        Task { await controller.connect(driver, lastInput: lastInput) }
    }

    // MARK: Panel

    func panelDidAppear() {
        controller.isPanelVisible = true
        Task {
            await controller.refresh()
            await refreshDeviceIfStale()
        }
    }

    func panelDidDisappear() {
        controller.isPanelVisible = false
    }

    /// The KEF maximum volume can change in KEF Connect at any time; it is read
    /// again when the panel or settings open, at most every 30 seconds.
    func refreshDeviceIfStale() async {
        if let deviceReadAt, deviceReadAt.duration(to: .now) < .seconds(30) { return }
        deviceReadAt = .now
        await controller.refreshDevice()
    }

    func openSettings(_ tab: SettingsTab? = nil, using open: OpenSettingsAction) {
        if let tab { settingsTab = tab }
        NSApp.activate()
        open()
    }

    // MARK: Shortcuts

    /// Saved as `{"volumeUp": {...}, "toggleMute": null}`: a missing action keeps its
    /// default, null turns it off.
    private static func loadShortcuts(_ data: Data?) -> [ShortcutAction: Shortcut] {
        var result = Shortcut.defaults
        guard let data, let saved = try? JSONDecoder().decode([String: Shortcut?].self, from: data) else { return result }
        for (name, shortcut) in saved {
            guard let action = ShortcutAction(rawValue: name) else { continue }
            result[action] = shortcut
        }
        return result
    }

    private func saveShortcuts() {
        var saved: [String: Shortcut?] = [:]
        for action in ShortcutAction.allCases { saved[action.rawValue] = shortcuts[action] }
        defaults.set(try? JSONEncoder().encode(saved), forKey: Keys.shortcuts)
    }

    /// Registers the shortcuts, except while one is being recorded. The self-test
    /// leaves the keyboard alone.
    func applyShortcuts() {
        guard started, recordingShortcut == nil, SelfTest.folder == nil else { return }
        shortcutFailures = HotkeyCenter.shared.register(shortcuts)
    }

    func resetShortcuts() {
        shortcuts = Shortcut.defaults
        saveShortcuts()
        applyShortcuts()
    }

    /// Settings' recorder: the next key press becomes the shortcut. Esc cancels,
    /// Delete turns the shortcut off.
    func startRecording(_ action: ShortcutAction) {
        stopRecording()
        recordingShortcut = action
        recordingError = nil
        HotkeyCenter.shared.unregisterAll()
        recordingMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            let code = event.keyCode
            let flags = event.modifierFlags
            let characters = event.charactersIgnoringModifiers
            let used = MainActor.assumeIsolated { self?.record(code, flags, characters) ?? false }
            return used ? nil : event
        }
    }

    func stopRecording() {
        if let recordingMonitor { NSEvent.removeMonitor(recordingMonitor) }
        recordingMonitor = nil
        guard recordingShortcut != nil else { return }
        recordingShortcut = nil
        recordingError = nil
        applyShortcuts()
    }

    private func record(_ code: UInt16, _ flags: NSEvent.ModifierFlags, _ characters: String?) -> Bool {
        guard let action = recordingShortcut else { return false }
        let plain = flags.isDisjoint(with: Shortcut.relevantModifiers)
        if plain, Int(code) == kVK_Escape {
            stopRecording()
            return true
        }
        if plain, Int(code) == kVK_Delete || Int(code) == kVK_ForwardDelete {
            shortcuts[action] = nil
            saveShortcuts()
            stopRecording()
            return true
        }
        switch Shortcut.recorded(keyCode: code, modifiers: flags, characters: characters) {
        case let .success(shortcut):
            if let other = ShortcutAction.allCases.first(where: { $0 != action && shortcuts[$0]?.sameKeys(as: shortcut) == true }) {
                recordingError = Shortcut.RecordingError.duplicate(other).message
                return true
            }
            shortcuts[action] = shortcut
            saveShortcuts()
            stopRecording()
        case let .failure(error):
            recordingError = error.message
        }
        return true
    }

    // MARK: Login item

    func refreshLoginItemStatus() {
        loginItemStatus = SMAppService.mainApp.status
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        loginItemError = nil
        do {
            if enabled {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            loginItemError = Bundle.main.bundleIdentifier == nil
                ? loc("앱 번들(.app)로 실행할 때만 설정할 수 있습니다", "Only available when running as an app bundle (.app)", "アプリバンドル（.app）として実行しているときだけ設定できます")
                : loc("로그인 항목을 변경하지 못했습니다: \(error.localizedDescription)", "Couldn't change the login item: \(error.localizedDescription)", "ログイン項目を変更できませんでした: \(error.localizedDescription)")
        }
        refreshLoginItemStatus()
    }

    // MARK: Sleep and wake

    private func observeSleep() {
        let center = NSWorkspace.shared.notificationCenter
        observers.append(center.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.controller.suspend() }
        })
        observers.append(center.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.controller.resume() }
        })
    }
}
