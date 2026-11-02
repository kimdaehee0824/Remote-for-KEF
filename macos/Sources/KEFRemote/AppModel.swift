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
    case shortcuts
}

/// One registered speaker while the app runs: its saved entry, its own controller and how reaching it went.
/// Only the selected speaker listens for change reports and is polled; the others are read while the speaker list
/// is open, so a house full of speakers costs no more network traffic than one.
@MainActor
@Observable
final class SpeakerSlot: Identifiable {
    fileprivate(set) var entry: SpeakerEntry
    let controller = SpeakerController()
    /// The last attempt to reach it failed, as opposed to not tried yet.
    fileprivate(set) var unreachable = false

    init(_ entry: SpeakerEntry) {
        self.entry = entry
        id = entry.id
        controller.listensForChanges = false
        controller.playbackEnabled = false
        controller.appLimit = entry.appLimit
        controller.approachFromAbove = entry.approachFromAbove
    }

    /// The entry's id, which never changes. Not isolated, as `Identifiable` asks.
    nonisolated let id: String

    /// What the speaker calls itself, else the last name it reported, else its address.
    var name: String { controller.device?.name.nonEmpty ?? SpeakerList.displayName(entry) }

    /// The model the speaker is drawn as and whose finishes are listed (`KefModels.shown`).
    var shownModel: String? { KefModels.shown(reported: controller.device?.model ?? entry.model, picked: entry.pickedModel) }

    /// The picked finish, if it is one of the shown model's.
    var finish: KefFinish? { KefFinish.find(entry.finish, model: shownModel) }

    /// The line under its name in the speaker list: the volume while it is on, else its state.
    var listStatus: String {
        let c = controller
        switch c.activity {
        case .powerOn?: return loc("켜는 중", "Turning on", "電源を入れています")
        case .powerOff?: return loc("끄는 중", "Turning off", "電源を切っています")
        default: break
        }
        if c.isWaking { return loc("켜는 중", "Turning on", "電源を入れています") }
        switch c.phase {
        case .online:
            guard let state = c.snapshot else { return loc("확인하는 중", "Checking", "確認中") }
            if state.power == .standby { return loc("대기", "Standby", "スタンバイ") }
            return state.muted ? loc("음소거", "Muted", "ミュート") : loc("음량 \(state.volume)", "Volume \(state.volume)", "音量 \(state.volume)")
        case .offline:
            return Self.notConnected
        case .idle, .connecting:
            return unreachable ? Self.notConnected : loc("확인하는 중", "Checking", "確認中")
        }
    }

    static var notConnected: String { loc("연결 안 됨", "Not connected", "未接続") }

    var isOn: Bool { controller.isOnline && controller.snapshot?.power == .on }
}

/// App-level state: saved settings, the registered speakers and system hooks.
/// The panel, menu bar, shortcuts and volume indicator work on the selected speaker.
@MainActor
@Observable
final class AppModel {
    static let shared = Demo.isOn
        ? AppModel(defaults: forgettingSpeakers(UserDefaults(suiteName: Demo.suite) ?? .standard), usesKeychain: false)
        : AppModel()

    /// `--demo` adds its speakers each time it starts, so none are kept from the last run.
    private static func forgettingSpeakers(_ defaults: UserDefaults) -> UserDefaults {
        for key in [Keys.speakers, Keys.selectedSpeaker, Keys.host] { defaults.removeObject(forKey: key) }
        return defaults
    }

    /// The registered speakers, in the order they were added.
    private(set) var speakers: [SpeakerSlot] = []
    private(set) var selectedID: String?
    /// Stands in while no speaker is registered, so the surfaces always have a controller to show.
    @ObservationIgnored private let none = SpeakerController()

    var selected: SpeakerSlot? { speakers.first { $0.id == selectedID } ?? speakers.first }

    /// The selected speaker's controller.
    var controller: SpeakerController { selected?.controller ?? none }

    /// More than one speaker: the surfaces say which one they work on and offer to switch.
    var hasSeveral: Bool { speakers.count > 1 }
    /// The speaker list beside the panel is open. The other speakers are read every `listRefresh` meanwhile.
    var speakerListOpen = false
    static let listRefresh: Duration = .seconds(3)

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

    /// Builds the driver for a speaker. The self-test and `--demo` put simulated speakers here.
    @ObservationIgnored var makeDriver: (@MainActor (SpeakerEntry, String) throws -> any SpeakerDriver)?

    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let usesKeychain: Bool
    @ObservationIgnored private var started = false
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var deviceReadAt: [String: ContinuousClock.Instant] = [:]
    @ObservationIgnored private var volumeKeys: VolumeKeys?
    @ObservationIgnored private var recordingMonitor: Any?
    @ObservationIgnored private var panelVisible = false

    private enum Keys {
        static let speakers = "speakers"
        static let selectedSpeaker = "selectedSpeaker"
        // 0.4 and earlier: one speaker. Read once to make the first entry, and left as they are.
        static let host = "speakerHost"
        static let appLimit = "appVolumeLimit"
        static let approachFromAbove = "approachFromAbove"
        static let speakerFinish = "speakerFinish"
        static let speakerModels = "speakerModels"
        static let lastInputs = "lastInputs"

        static let showVolume = "showVolumeInMenuBar"
        static let showNowPlaying = "showNowPlaying"
        static let shortcuts = "shortcuts"
        static let volumeKeyStep = "volumeKeyStep"
        static let showVolumeHUD = "showVolumeHUD"
    }

    /// The self-test passes its own defaults and keeps away from the keychain.
    init(defaults: UserDefaults = .standard, usesKeychain: Bool = true) {
        self.defaults = defaults
        self.usesKeychain = usesKeychain
        showsVolumeInMenuBar = defaults.object(forKey: Keys.showVolume) as? Bool ?? true
        showsNowPlaying = defaults.object(forKey: Keys.showNowPlaying) as? Bool ?? true
        shortcuts = Self.loadShortcuts(defaults.data(forKey: Keys.shortcuts))
        let step = defaults.object(forKey: Keys.volumeKeyStep) as? Int ?? 1
        volumeKeyStep = VolumeSteps.choices.contains(step) ? step : 1
        showsVolumeHUD = defaults.object(forKey: Keys.showVolumeHUD) as? Bool ?? true

        let entries: [SpeakerEntry]
        if let data = defaults.data(forKey: Keys.speakers), let saved = try? JSONDecoder().decode([SpeakerEntry].self, from: data) {
            entries = saved
        } else {
            entries = SpeakerList.migrated(from: Self.legacySpeaker(defaults))
        }
        speakers = entries.map(makeSlot)
        selectedID = SpeakerList.selected(entries, id: defaults.string(forKey: Keys.selectedSpeaker))?.id
        saveSpeakers()
        if let selected {
            selected.controller.listensForChanges = true
            selected.controller.playbackEnabled = showsNowPlaying
        }
        refreshLoginItemStatus()
    }

    private static func legacySpeaker(_ defaults: UserDefaults) -> LegacySpeaker? {
        guard let host = defaults.string(forKey: Keys.host) else { return nil }
        return LegacySpeaker(
            host: host,
            appLimit: defaults.object(forKey: Keys.appLimit) != nil ? defaults.integer(forKey: Keys.appLimit) : nil,
            approachFromAbove: defaults.bool(forKey: Keys.approachFromAbove),
            finish: defaults.string(forKey: Keys.speakerFinish),
            pickedModel: (defaults.dictionary(forKey: Keys.speakerModels) as? [String: String])?[host],
            lastInput: (defaults.dictionary(forKey: Keys.lastInputs) as? [String: String])?[host]
        )
    }

    private func makeSlot(_ entry: SpeakerEntry) -> SpeakerSlot {
        let slot = SpeakerSlot(entry)
        slot.controller.lastActiveSourceChanged = { [weak self, weak slot] source in
            guard let self, let slot, slot.entry.lastInput != source else { return }
            self.update(slot) { $0.lastInput = source }
        }
        return slot
    }

    private func saveSpeakers() {
        defaults.set(try? JSONEncoder().encode(speakers.map(\.entry)), forKey: Keys.speakers)
        defaults.set(selectedID, forKey: Keys.selectedSpeaker)
    }

    /// Changes a speaker's saved entry and applies what the controller uses.
    func update(_ slot: SpeakerSlot, _ change: (inout SpeakerEntry) -> Void) {
        var entry = slot.entry
        change(&entry)
        entry.appLimit = SpeakerList.clampLimit(entry.appLimit)
        guard entry != slot.entry else { return }
        slot.entry = entry
        slot.controller.appLimit = entry.appLimit
        slot.controller.approachFromAbove = entry.approachFromAbove
        saveSpeakers()
    }

    func start() {
        guard !started else { return }
        started = true
        observeSleep()
        volumeKeys = VolumeKeys(model: self)
        applyShortcuts()
        if let selected {
            selected.controller.startMonitoring()
            connect(selected)
        }
    }

    // MARK: The selected speaker

    /// The model picked in settings for the selected speaker, or nil.
    var pickedModel: String? {
        get { selected?.entry.pickedModel }
        set { if let selected { update(selected) { $0.pickedModel = newValue } } }
    }

    var shownModel: String? { selected?.shownModel }

    /// Finish the selected speaker is drawn in (KefFinish.id), or nil for the app's own look.
    var speakerFinishID: String? {
        get { selected?.entry.finish }
        set { if let selected { update(selected) { $0.finish = newValue } } }
    }

    var speakerFinish: KefFinish? { selected?.finish }

    var approachFromAbove: Bool {
        get { selected?.entry.approachFromAbove ?? false }
        set { if let selected { update(selected) { $0.approachFromAbove = newValue } } }
    }

    var appLimit: Int {
        get { selected?.entry.appLimit ?? VolumePolicy.defaultAppLimit }
        set { if let selected { update(selected) { $0.appLimit = newValue } } }
    }

    // MARK: Speakers

    func hasPassword(_ slot: SpeakerSlot) -> Bool {
        usesKeychain && KeychainStore.password(account: slot.entry.keychainAccount) != nil
    }

    /// Makes `slot` the speaker the panel, menu bar and shortcuts work on.
    func select(_ slot: SpeakerSlot) {
        guard slot.id != selected?.id, speakers.contains(where: { $0 === slot }) else { return }
        deactivate(selected)
        activate(slot)
    }

    /// The speaker that is no longer the selected one stops listening and being polled.
    private func deactivate(_ slot: SpeakerSlot?) {
        guard let c = slot?.controller else { return }
        c.stopMonitoring()
        c.listensForChanges = false
        c.playbackEnabled = false
        c.isPanelVisible = false
    }

    /// The selected speaker listens for change reports, is polled and reads what is playing.
    private func activate(_ slot: SpeakerSlot) {
        selectedID = slot.id
        saveSpeakers()
        let c = slot.controller
        c.listensForChanges = true
        c.playbackEnabled = showsNowPlaying
        c.isPanelVisible = panelVisible
        if started { c.startMonitoring() }
        if c.phase == .idle {
            connect(slot)
        } else {
            Task { await c.refresh() }
        }
    }

    /// The "next speaker" shortcut.
    @discardableResult
    func selectNext() -> SpeakerSlot? {
        guard let entry = SpeakerList.next(speakers.map(\.entry), after: selected?.id),
              let next = speakers.first(where: { $0.id == entry.id }) else { return nil }
        select(next)
        return next
    }

    /// Validates the address, saves the password and registers the speaker, then connects read-only.
    /// The first one becomes the selected one. Returns the new speaker, or why it wasn't added.
    func addSpeaker(host input: String, password: String) -> Result<SpeakerSlot, AddError> {
        guard let host = KefW2Driver.validatedHost(input) else { return .failure(.message(KefError.invalidHost.description)) }
        if let other = SpeakerList.withHost(host, in: speakers.map(\.entry)) {
            return .failure(.message(loc("이미 추가한 스피커입니다: \(SpeakerList.displayName(other))", "Already added: \(SpeakerList.displayName(other))", "すでに追加したスピーカーです: \(SpeakerList.displayName(other))")))
        }
        let entry = SpeakerEntry(host: host)
        if let error = storePassword(password.isEmpty ? nil : password, account: entry.keychainAccount, removeWhenEmpty: false) {
            return .failure(.message(error))
        }
        let slot = makeSlot(entry)
        speakers.append(slot)
        if speakers.count == 1 {
            activate(slot)
        } else {
            saveSpeakers()
            connect(slot)
        }
        return .success(slot)
    }

    enum AddError: Error {
        case message(String)
    }

    /// Validates and saves a speaker's address, then reconnects read-only.
    /// `password` nil keeps the stored one; an empty string removes it.
    func saveConnection(_ slot: SpeakerSlot, host input: String, password: String?) -> String? {
        guard let host = KefW2Driver.validatedHost(input) else { return KefError.invalidHost.description }
        if let other = SpeakerList.withHost(host, in: speakers.map(\.entry), except: slot.id) {
            return loc("이미 추가한 스피커입니다: \(SpeakerList.displayName(other))", "Already added: \(SpeakerList.displayName(other))", "すでに追加したスピーカーです: \(SpeakerList.displayName(other))")
        }
        if let error = storePassword(password, account: slot.entry.keychainAccount, removeWhenEmpty: true) { return error }
        update(slot) { $0.host = host }
        connect(slot)
        return nil
    }

    private func storePassword(_ password: String?, account: String, removeWhenEmpty: Bool) -> String? {
        guard usesKeychain, let password else { return nil }
        do {
            if password.isEmpty {
                if removeWhenEmpty { try KeychainStore.deletePassword(account: account) }
            } else {
                try KeychainStore.setPassword(password, account: account)
            }
            return nil
        } catch {
            return loc("암호를 키체인에 저장하지 못했습니다", "Couldn't save the password to the keychain", "パスワードをキーチェーンに保存できませんでした")
        }
    }

    /// Forgets a speaker and its password. When it was the selected one, the one before it takes over.
    func remove(_ slot: SpeakerSlot) {
        guard speakers.contains(where: { $0 === slot }) else { return }
        let wasSelected = slot.id == selected?.id
        let newSelection = SpeakerList.removing(slot.id, from: speakers.map(\.entry), selected: selected?.id).selected
        deactivate(slot)
        slot.controller.disconnect()
        if usesKeychain { try? KeychainStore.deletePassword(account: slot.entry.keychainAccount) }
        speakers.removeAll { $0 === slot }
        deviceReadAt[slot.id] = nil
        selectedID = newSelection
        if wasSelected, let next = selected {
            activate(next)
        } else {
            saveSpeakers()
        }
    }

    /// Reconnects the selected speaker, e.g. from "Reconnect" in the panel.
    func connect() {
        if let selected { connect(selected) }
    }

    /// Building a new driver invalidates everything queued for the old one.
    func connect(_ slot: SpeakerSlot) {
        guard let driver = makeDriver(for: slot) else {
            slot.controller.disconnect()
            return
        }
        deviceReadAt[slot.id] = .now
        let lastInput = slot.entry.lastInput
        Task {
            await slot.controller.connect(driver, lastInput: lastInput)
            settled(slot)
        }
    }

    private func makeDriver(for slot: SpeakerSlot) -> (any SpeakerDriver)? {
        let entry = slot.entry
        guard entry.isConfigured else { return nil }
        let password = usesKeychain ? KeychainStore.password(account: entry.keychainAccount) ?? "" : ""
        if let makeDriver { return try? makeDriver(entry, password) }
        return try? KefW2Driver(host: entry.host, password: password)
    }

    /// After a connect or read: whether it was reached, and its name and model for the list.
    private func settled(_ slot: SpeakerSlot) {
        guard speakers.contains(where: { $0 === slot }) else { return }
        slot.unreachable = !slot.controller.isOnline
        if let device = slot.controller.device, slot.entry.name != device.name || slot.entry.model != device.model {
            update(slot) {
                $0.name = device.name
                $0.model = device.model
            }
        }
    }

    /// Reads every speaker but the selected one, which is kept up to date anyway: the list shows them.
    func refreshOthers() async {
        let reads = speakers.filter { $0.id != selected?.id }.map { slot in Task { await refresh(slot) } }
        for read in reads { await read.value }
    }

    /// Connects to or reads one speaker now.
    func refresh(_ slot: SpeakerSlot) async {
        let c = slot.controller
        if c.phase == .idle {
            guard let driver = makeDriver(for: slot) else { return }
            deviceReadAt[slot.id] = .now
            await c.connect(driver, lastInput: slot.entry.lastInput)
        } else if !c.isBusy {
            await c.refresh()
        }
        settled(slot)
    }

    /// The power switch of a row in the speaker list. Leaves the selection as it is.
    func setPower(_ slot: SpeakerSlot, on: Bool) async {
        if !slot.controller.isOnline { await refresh(slot) }
        guard slot.controller.isOnline else { return }
        if on { slot.controller.wake() } else { slot.controller.standby() }
    }

    /// "Turn all off": puts every speaker that is on in standby, the ones not read yet included.
    func turnAllOff() async {
        let offs = speakers.map { slot in Task { await turnOff(slot) } }
        for off in offs { await off.value }
    }

    private func turnOff(_ slot: SpeakerSlot) async {
        if !slot.controller.isOnline { await refresh(slot) }
        guard slot.isOn, slot.controller.device?.capabilities.contains(.power) == true else { return }
        slot.controller.standby()
    }

    // MARK: Panel

    func panelDidAppear() {
        panelVisible = true
        controller.isPanelVisible = true
        Task {
            await controller.refresh()
            await refreshDeviceIfStale()
        }
    }

    func panelDidDisappear() {
        panelVisible = false
        speakerListOpen = false
        controller.isPanelVisible = false
    }

    /// The KEF maximum volume can change in KEF Connect at any time; it is read
    /// again when the panel or settings open, at most every 30 seconds.
    func refreshDeviceIfStale(_ slot: SpeakerSlot? = nil) async {
        guard let slot = slot ?? selected else { return }
        if let readAt = deviceReadAt[slot.id], readAt.duration(to: .now) < .seconds(30) { return }
        deviceReadAt[slot.id] = .now
        await slot.controller.refreshDevice()
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
            MainActor.assumeIsolated {
                for slot in self?.speakers ?? [] { slot.controller.suspend() }
            }
        })
        observers.append(center.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                for slot in self?.speakers ?? [] { slot.controller.resume() }
            }
        })
    }
}
