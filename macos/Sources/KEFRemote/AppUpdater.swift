import AppKit
import Observation
import Sparkle

/// Updates with Sparkle 2 from the GitHub releases. Info.plist points SUFeedURL at the latest release's
/// appcast.xml and carries the EdDSA public key that appcast is signed with; SUEnableAutomaticChecks makes
/// the daily check the default without Sparkle's permission prompt.
///
/// A menu bar app is always in the background, so an update found by the daily check would open behind other
/// windows. Instead it waits as a line in the panel (Sparkle's gentle reminders) until the user opens it.
@MainActor
@Observable
final class AppUpdater: NSObject {
    static let shared = AppUpdater()

    /// Version found by a scheduled check, shown in the panel until the user looks at it.
    private(set) var waitingVersion: String?
    /// Sparkle runs: a bundled app with a feed and a key, outside the self-test and demo.
    private(set) var isRunning = false

    var checksAutomatically = true {
        didSet { controller?.updater.automaticallyChecksForUpdates = checksAutomatically }
    }
    var downloadsAutomatically = false {
        didSet { controller?.updater.automaticallyDownloadsUpdates = downloadsAutomatically }
    }

    @ObservationIgnored private var controller: SPUStandardUpdaterController?

    /// `swift run` has no Info.plist, so no feed and no key: the updater stays off instead of failing to start.
    var isConfigured: Bool {
        let info = Bundle.main.infoDictionary ?? [:]
        return (info["SUFeedURL"] as? String)?.isEmpty == false && (info["SUPublicEDKey"] as? String)?.isEmpty == false
    }

    var currentVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
    }

    func start() {
        guard controller == nil, isConfigured else { return }
        let controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: nil, userDriverDelegate: self)
        self.controller = controller
        isRunning = true
        checksAutomatically = controller.updater.automaticallyChecksForUpdates
        downloadsAutomatically = controller.updater.automaticallyDownloadsUpdates
    }

    /// Sparkle's own window: checking, then the release notes and Install, or "You're up to date".
    func checkForUpdates() {
        waitingVersion = nil
        NSApp.activate()
        controller?.checkForUpdates(nil)
    }
}

extension AppUpdater: @preconcurrency SPUStandardUserDriverDelegate {
    var supportsGentleScheduledUpdateReminders: Bool { true }

    /// Right after launch Sparkle may show the alert in front; any later find waits in the panel.
    func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem, andInImmediateFocus immediateFocus: Bool) -> Bool {
        immediateFocus
    }

    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem, state: SPUUserUpdateState) {
        if !handleShowingUpdate { waitingVersion = update.displayVersionString }
    }

    func standardUserDriverDidReceiveUserAttention(forUpdate update: SUAppcastItem) {
        waitingVersion = nil
    }

    func standardUserDriverWillFinishUpdateSession() {
        waitingVersion = nil
    }
}
