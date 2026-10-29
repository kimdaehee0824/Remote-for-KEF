import AppKit
import KefRemoteCore

/// `--demo`: the normal menu bar app against the simulated speaker of the self-test,
/// with its own settings and no keychain. The panel opens by itself, so screenshots
/// can be taken with the system's own tools, Liquid Glass included.
@MainActor
enum Demo {
    static let isOn = ProcessInfo.processInfo.arguments.contains("--demo")
    static let suite = "io.github.kimdaehee0824.KEFRemote.Demo"

    static func start(_ model: AppModel) {
        model.start()
        model.connect(using: DemoDriver(DemoSpeaker()))
        Task {
            try? await Task.sleep(for: .seconds(2))
            openPanel()
        }
    }

    /// Clicks the menu bar icon, the way a person would.
    private static func openPanel() {
        for window in NSApp.windows {
            if let button = statusButton(in: window.contentView) {
                button.performClick(nil)
                return
            }
        }
    }

    private static func statusButton(in view: NSView?) -> NSStatusBarButton? {
        guard let view else { return nil }
        if let button = view as? NSStatusBarButton { return button }
        for subview in view.subviews {
            if let button = statusButton(in: subview) { return button }
        }
        return nil
    }
}
