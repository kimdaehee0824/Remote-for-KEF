import AppKit
import KefRemoteCore

/// `--demo`: the normal menu bar app against the simulated speaker of the self-test,
/// with its own settings and no keychain, for screenshots taken with the system's own
/// tools, Liquid Glass included. A plain backdrop covers the desktop, so a window
/// screenshot of the panel shows nothing else. `--appearance light|dark` picks the look
/// without changing the system's.
@MainActor
enum Demo {
    static let isOn = ProcessInfo.processInfo.arguments.contains("--demo")
    static let suite = "io.github.kimdaehee0824.KEFRemote.Demo"

    private static var backdrop: NSWindow?

    static func start(_ model: AppModel) {
        applyAppearance(ProcessInfo.processInfo.arguments)
        showBackdrop()
        model.start()
        model.connect(using: DemoDriver(DemoSpeaker()))
        Task {
            try? await Task.sleep(for: .seconds(2))
            openPanel()
        }
    }

    private static func applyAppearance(_ arguments: [String]) {
        guard let index = arguments.firstIndex(of: "--appearance"), index + 1 < arguments.count else { return }
        switch arguments[index + 1] {
        case "light": NSApp.appearance = NSAppearance(named: .aqua)
        case "dark": NSApp.appearance = NSAppearance(named: .darkAqua)
        default: break
        }
    }

    /// The README background color, under the menu bar and above every other window.
    private static func showBackdrop() {
        guard let screen = NSScreen.main else { return }
        let window = NSWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.backgroundColor = NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
                ? NSColor(srgbRed: 0x2C / 255, green: 0x2C / 255, blue: 0x2E / 255, alpha: 1)
                : NSColor(srgbRed: 0xF3 / 255, green: 0xF2 / 255, blue: 0xF8 / 255, alpha: 1)
        }
        window.isReleasedWhenClosed = false
        window.ignoresMouseEvents = true
        window.collectionBehavior = [.canJoinAllSpaces, .stationary]
        window.orderFrontRegardless()
        backdrop = window
    }

    /// Clicks the menu bar icon, the way a person would. On macOS 27 the icon is drawn
    /// outside the app and this does nothing, so the icon has to be clicked by hand.
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
