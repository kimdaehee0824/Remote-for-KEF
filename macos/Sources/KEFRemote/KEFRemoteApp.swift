import AppKit
import KefRemoteCore
import SwiftUI

@main
struct KEFRemoteApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = AppModel.shared

    var body: some Scene {
        // The self-test draws its own windows and leaves the menu bar alone.
        MenuBarExtra(isInserted: .constant(SelfTest.folder == nil)) {
            MenuPanel()
                .environment(model)
        } label: {
            MenuBarLabel(model: model)
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView()
                .environment(model)
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) {
        // Menu bar only. Info.plist sets LSUIElement too; this covers `swift run`.
        NSApp.setActivationPolicy(.accessory)
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let folder = SelfTest.folder {
            Task {
                let passed = await SelfTest.run(in: folder)
                exit(passed ? 0 : 1)
            }
        } else {
            AppModel.shared.start()
        }
    }
}

/// Uni-Q speaker glyph and the volume (design M1, 1). Not the system Sound
/// symbol, so the Mac volume and the speaker volume are never confused.
struct MenuBarLabel: View {
    let model: AppModel

    var body: some View {
        let shown = Self.shown(model)
        Image(nsImage: MenuBarGlyph.label(shown.glyph, volume: shown.volume))
            .accessibilityLabel(accessibilityText)
    }

    /// Keeps the last known state while reconnecting after sleep, so the icon does not flicker.
    static func shown(_ model: AppModel) -> (glyph: MenuBarGlyph, volume: Int?) {
        let controller = model.controller
        guard let state = controller.snapshot, controller.phase == .online || controller.phase == .connecting else {
            return (.offline, nil)
        }
        if state.power == .standby { return (.standby, nil) }
        if state.muted { return (.muted, nil) }
        return (.on, model.showsVolumeInMenuBar ? controller.pendingVolume ?? state.volume : nil)
    }

    private var accessibilityText: String {
        let controller = model.controller
        guard let state = controller.snapshot, controller.isOnline else { return "Remote for KEF, 연결 안 됨" }
        if state.power == .standby { return "Remote for KEF, 대기" }
        return state.muted ? "Remote for KEF, 음소거" : "Remote for KEF, 음량 \(state.volume)"
    }
}
