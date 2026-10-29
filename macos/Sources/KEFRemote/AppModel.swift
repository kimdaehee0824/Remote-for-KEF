import AppKit
import KefRemoteCore
import KefW2Driver
import Observation
import ServiceManagement
import SwiftUI

enum SettingsTab: Hashable {
    case general
    case speaker
    case volume
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
    /// Tab the settings window shows when it opens next.
    var settingsTab: SettingsTab = .general
    private(set) var loginItemStatus: SMAppService.Status = .notRegistered
    private(set) var loginItemError: String?

    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let usesKeychain: Bool
    @ObservationIgnored private var started = false
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var deviceReadAt: ContinuousClock.Instant?

    private enum Keys {
        static let host = "speakerHost"
        static let appLimit = "appVolumeLimit"
        static let showVolume = "showVolumeInMenuBar"
        static let approachFromAbove = "approachFromAbove"
        static let speakerFinish = "speakerFinish"
    }

    /// The self-test passes its own defaults and keeps away from the keychain.
    init(defaults: UserDefaults = .standard, usesKeychain: Bool = true) {
        self.defaults = defaults
        self.usesKeychain = usesKeychain
        host = defaults.string(forKey: Keys.host) ?? ""
        hasPassword = usesKeychain && KeychainStore.password() != nil
        showsVolumeInMenuBar = defaults.object(forKey: Keys.showVolume) as? Bool ?? true
        approachFromAbove = defaults.bool(forKey: Keys.approachFromAbove)
        speakerFinishID = defaults.string(forKey: Keys.speakerFinish)
        controller.approachFromAbove = approachFromAbove
        if defaults.object(forKey: Keys.appLimit) != nil {
            controller.appLimit = Self.clampLimit(defaults.integer(forKey: Keys.appLimit))
        }
        refreshLoginItemStatus()
    }

    func start() {
        guard !started else { return }
        started = true
        observeSleep()
        controller.startMonitoring()
        if !host.isEmpty { connect() }
    }

    /// The picked finish, unless it belongs to a different model than the connected speaker.
    var speakerFinish: KefFinish? {
        KefFinish.find(speakerFinishID, model: controller.device?.model)
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
                return "암호를 키체인에 저장하지 못했습니다"
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
        Task { await controller.connect(driver) }
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
                ? "앱 번들(.app)로 실행할 때만 설정할 수 있습니다"
                : "로그인 항목을 변경하지 못했습니다: \(error.localizedDescription)"
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
