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
        } else if Demo.isOn {
            Demo.start(AppModel.shared)
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
        MenuBarIcon(shown: Self.shown(model))
            .equatable()
            .accessibilityLabel(accessibilityText)
    }

    enum Shown: Equatable {
        case connecting
        case glyph(MenuBarGlyph, volume: Int?)
    }

    /// Keeps the last known state while reconnecting after sleep, so the icon does not flicker.
    /// Starting up shows the faint glyph, not the offline one.
    static func shown(_ model: AppModel) -> Shown {
        let controller = model.controller
        guard let state = controller.snapshot, controller.phase == .online || controller.phase == .connecting else {
            return controller.phase == .connecting ? .connecting : .glyph(.offline, volume: nil)
        }
        if state.power == .standby { return .glyph(.standby, volume: nil) }
        if state.muted { return .glyph(.muted, volume: nil) }
        return .glyph(.on, volume: model.showsVolumeInMenuBar ? controller.pendingVolume ?? state.volume : nil)
    }

    private var accessibilityText: String {
        let controller = model.controller
        if controller.snapshot == nil, controller.phase == .connecting { return "Remote for KEF, 연결하는 중" }
        guard let state = controller.snapshot, controller.isOnline else { return "Remote for KEF, 연결 안 됨" }
        if state.power == .standby { return "Remote for KEF, 대기" }
        return state.muted ? "Remote for KEF, 음소거" : "Remote for KEF, 음량 \(state.volume)"
    }
}

/// The image itself. Equatable, so the status item is only given a new image
/// when what it shows changes.
private struct MenuBarIcon: View, Equatable {
    let shown: MenuBarLabel.Shown

    var body: some View {
        switch shown {
        case .connecting:
            Image(nsImage: MenuBarGlyph.connecting)
        case let .glyph(glyph, volume):
            Image(nsImage: MenuBarGlyph.label(glyph, volume: volume))
        }
    }
}
