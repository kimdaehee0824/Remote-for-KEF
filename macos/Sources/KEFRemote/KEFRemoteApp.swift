import AppKit
import SwiftUI

@main
struct KEFRemoteApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model = AppModel.shared

    var body: some Scene {
        MenuBarExtra {
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
        AppModel.shared.start()
    }
}

/// Speaker symbol whose waves fill with the volume, like the system Sound menu.
struct MenuBarLabel: View {
    let model: AppModel

    var body: some View {
        let controller = model.controller
        let snapshot = controller.snapshot
        HStack(spacing: 3) {
            switch (controller.phase, snapshot) {
            case let (.online, state?) where state.power == .standby:
                Image(systemName: "powersleep")
            case let (.online, state?) where state.muted:
                Image(systemName: "speaker.slash.fill")
            case let (.online, state?):
                Image(systemName: "speaker.wave.3.fill", variableValue: Double(state.volume) / 100)
                if model.showsVolumeInMenuBar {
                    Text("\(controller.pendingVolume ?? state.volume)").monospacedDigit()
                }
            default:
                Image(systemName: "hifispeaker")
            }
        }
        .accessibilityLabel(accessibilityText)
    }

    private var accessibilityText: String {
        guard let state = model.controller.snapshot, model.controller.isOnline else { return "KEF Remote, 연결 안 됨" }
        return state.muted ? "KEF Remote, 음소거" : "KEF Remote, 음량 \(state.volume)"
    }
}
