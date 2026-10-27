import AppKit
import KefRemoteCore
import KefW2Driver
import Observation
import ServiceManagement

/// App-level state: saved settings, the speaker controller and system hooks.
@MainActor
@Observable
final class AppModel {
    static let shared = AppModel()

    let controller = SpeakerController()

    private(set) var host: String
    private(set) var hasPassword: Bool
    var showsVolumeInMenuBar: Bool {
        didSet { UserDefaults.standard.set(showsVolumeInMenuBar, forKey: Keys.showVolume) }
    }
    private(set) var loginItemStatus: SMAppService.Status = .notRegistered
    private(set) var loginItemError: String?

    @ObservationIgnored private var started = false
    @ObservationIgnored private var observers: [NSObjectProtocol] = []

    private enum Keys {
        static let host = "speakerHost"
        static let appLimit = "appVolumeLimit"
        static let showVolume = "showVolumeInMenuBar"
    }

    init() {
        let defaults = UserDefaults.standard
        host = defaults.string(forKey: Keys.host) ?? ""
        hasPassword = KeychainStore.password() != nil
        showsVolumeInMenuBar = defaults.object(forKey: Keys.showVolume) as? Bool ?? true
        if defaults.object(forKey: Keys.appLimit) != nil {
            controller.appLimit = defaults.integer(forKey: Keys.appLimit)
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

    var appLimit: Int {
        get { controller.appLimit }
        set {
            controller.appLimit = min(100, max(0, newValue))
            UserDefaults.standard.set(controller.appLimit, forKey: Keys.appLimit)
        }
    }

    /// Validates and saves the address, then reconnects read-only.
    /// `password` nil keeps the stored one; an empty string removes it.
    func saveConnection(host input: String, password: String?) -> String? {
        guard let normalized = KefW2Driver.validatedHost(input) else {
            return KefError.invalidHost.description
        }
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
        host = normalized
        hasPassword = KeychainStore.password() != nil
        UserDefaults.standard.set(normalized, forKey: Keys.host)
        connect()
        return nil
    }

    /// Building a new driver invalidates everything queued for the old one.
    func connect() {
        guard !host.isEmpty, let driver = try? KefW2Driver(host: host, password: KeychainStore.password() ?? "") else {
            controller.disconnect()
            return
        }
        Task { await controller.connect(driver) }
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
