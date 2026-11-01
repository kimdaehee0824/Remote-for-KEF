import AppKit
import KefRemoteCore
import SwiftUI

/// Turns shortcut presses into controller commands and shows the result. The
/// controller stays the only place that talks to the speaker: it keeps the latest
/// volume target while a write is under way, so a held key sends one value at a time.
@MainActor
final class VolumeKeys {
    private let model: AppModel
    private let hud: VolumeHUD

    init(model: AppModel) {
        self.model = model
        hud = VolumeHUD(model: model)
        HotkeyCenter.shared.onPress = { [weak self] action in self?.press(action) }
    }

    func press(_ action: ShortcutAction) {
        let controller = model.controller
        guard controller.isOnline, let state = controller.snapshot, controller.device != nil else {
            hud.show(.problem(controller.phase == .connecting
                ? loc("스피커에 연결하는 중입니다", "Connecting to the speaker", "スピーカーに接続しています")
                : loc("스피커에 연결되어 있지 않습니다", "The speaker isn't connected", "スピーカーに接続されていません")))
            return
        }
        guard state.power == .on, !controller.isWaking else {
            hud.show(.problem(loc("스피커가 대기 상태입니다", "The speaker is in standby", "スピーカーはスタンバイ中です")))
            return
        }
        let limit = controller.effectiveLimit
        switch action {
        case .volumeUp, .volumeDown:
            let direction = action == .volumeUp ? 1 : -1
            let current = controller.pendingVolume ?? state.volume
            let target = VolumeSteps.next(current: current, direction: direction, step: model.volumeKeyStep, limit: limit)
            // Volume up while muted also unmutes, like the Mac's own volume keys. Both go
            // as one command, so a held key can't leave the volume behind a separate unmute.
            if direction > 0, state.muted {
                controller.setVolumeUnmuting(target)
            } else if target != current {
                controller.setVolume(target)
            }
            if model.showsVolumeHUD { hud.show(.level) }
        case .toggleMute:
            controller.toggleMute()
            if model.showsVolumeHUD { hud.show(.level) }
        }
    }
}

/// The volume indicator, built like the system's on macOS 26 and later from the
/// system's own parts: a glass capsule (`NSGlassEffectView`) holding the speaker
/// symbol and a standard slider. Size and place are measured on macOS 27: 308 × 66 pt,
/// 10 pt under the menu bar and 28.5 pt from the right edge, the slider 214 pt wide
/// from 62.5 pt. It fades in, stays about a second and fades out, and never takes
/// focus or clicks.
@MainActor
final class VolumeHUD {
    enum Content: Equatable {
        /// The speaker's volume, live from the controller.
        case level
        /// Why nothing changed, in place of the slider.
        case problem(String)
    }

    /// How long the indicator stays after the last press before it starts to go.
    static let shownFor: Duration = .milliseconds(1150)
    static let inDuration = 0.2
    static let outDuration = 0.25

    private let model: AppModel
    private let state = HUDState()
    private var panel: NSPanel?
    private var hideTask: Task<Void, Never>?
    private var shownCount = 0
    private var hiding = false

    init(model: AppModel) {
        self.model = model
    }

    func show(_ content: Content) {
        state.content = content
        let panel = panel ?? makePanel()
        self.panel = panel
        place(panel)
        shownCount += 1
        if !panel.isVisible {
            panel.alphaValue = 0
            panel.orderFrontRegardless()
        }
        if hiding || panel.alphaValue < 1 {
            hiding = false
            // The window fades as a whole, so the glass inside keeps drawing as it does at rest.
            NSAnimationContext.runAnimationGroup { context in
                context.duration = Self.inDuration
                context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                panel.animator().alphaValue = 1
            }
        }
        hideTask?.cancel()
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: Self.shownFor)
            guard !Task.isCancelled else { return }
            self?.hide()
        }
    }

    private func hide() {
        guard let panel else { return }
        let count = shownCount
        hiding = true
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Self.outDuration
            context.timingFunction = CAMediaTimingFunction(name: .easeIn)
            panel.animator().alphaValue = 0
        }
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(Self.outDuration + 0.05))
            // Unless another press brought it back meanwhile.
            guard let self, count == self.shownCount, self.hiding else { return }
            self.hiding = false
            self.panel?.orderOut(nil)
        }
    }

    private func makePanel() -> NSPanel {
        let size = HUDView.size
        let panel = NSPanel(
            contentRect: CGRect(origin: .zero, size: size),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered, defer: true
        )
        panel.isFloatingPanel = true
        panel.level = .statusBar
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        let host = NSHostingView(rootView: HUDView(model: model, state: state))
        host.sizingOptions = []
        host.frame = CGRect(origin: .zero, size: size)
        host.autoresizingMask = [.width, .height]
        panel.contentView = Self.capsule(holding: host, size: size)
        return panel
    }

    /// The system's glass on macOS 26 and later; the HUD material before that.
    private static func capsule(holding content: NSView, size: CGSize) -> NSView {
        let radius = size.height / 2
        #if compiler(>=6.2)
        if #available(macOS 26, *) {
            let glass = NSGlassEffectView(frame: CGRect(origin: .zero, size: size))
            glass.cornerRadius = radius
            glass.contentView = content
            return glass
        }
        #endif
        let effect = NSVisualEffectView(frame: CGRect(origin: .zero, size: size))
        effect.material = .hudWindow
        effect.blendingMode = .behindWindow
        effect.state = .active
        effect.maskImage = NSImage(size: size, flipped: false) { rect in
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        content.frame = effect.bounds
        effect.addSubview(content)
        return effect
    }

    /// On the screen with the active menu bar, under it at the right, where the system's own indicator appears.
    private func place(_ panel: NSPanel) {
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let size = HUDView.size
        let origin = CGPoint(
            x: screen.frame.maxX - HUDView.rightInset - size.width,
            y: screen.visibleFrame.maxY - HUDView.topInset - size.height
        )
        panel.setFrame(CGRect(origin: origin, size: size), display: false)
    }
}

@MainActor
@Observable
final class HUDState {
    var content: VolumeHUD.Content = .level
}

/// What sits on the glass: the speaker symbol and a standard slider that follows
/// the volume, placed as measured on macOS 27. The slider only shows the volume;
/// the window ignores the mouse.
struct HUDView: View {
    static let size = CGSize(width: 308, height: 66)
    static let rightInset: CGFloat = 28.5
    static let topInset: CGFloat = 10
    /// The speaker's body starts here; the slashed symbol 2.5 pt earlier.
    static let symbolInset: CGFloat = 20.5
    static let sliderLeading: CGFloat = 62.5
    static let sliderWidth: CGFloat = 214

    let model: AppModel
    let state: HUDState

    var body: some View {
        let controller = model.controller
        let volume = controller.pendingVolume ?? controller.snapshot?.volume ?? 0
        let muted = muteShown(controller)
        ZStack(alignment: .leading) {
            switch state.content {
            case .level:
                let symbol = Self.symbol(volume: volume, muted: muted)
                Image(systemName: symbol)
                    .resizable()
                    .fontWeight(.semibold)
                    .aspectRatio(contentMode: .fit)
                    .frame(height: Self.symbolHeight(symbol))
                    .foregroundStyle(.primary)
                    .padding(.leading, muted ? Self.symbolInset - 2.5 : Self.symbolInset)
                Slider(value: .constant(Double(muted ? 0 : volume)), in: 0...100)
                    .labelsHidden()
                    .tint(.primary)
                    .frame(width: Self.sliderWidth)
                    .padding(.leading, Self.sliderLeading)
                    .allowsHitTesting(false)
            case let .problem(text):
                Image(systemName: "exclamationmark.circle.fill")
                    .font(.system(size: 20, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .padding(.leading, Self.symbolInset)
                Text(text)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                    .frame(width: Self.sliderWidth, alignment: .leading)
                    .padding(.leading, Self.sliderLeading)
            }
        }
        .frame(width: Self.size.width, height: Self.size.height, alignment: .leading)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(loc("음량", "Volume", "音量"))
        .accessibilityValue(accessibilityValue(volume: volume, muted: muted))
    }

    /// The mute state the speaker is heading for while a mute command runs, or
    /// while volume up is unmuting.
    private func muteShown(_ controller: SpeakerController) -> Bool {
        if controller.pendingUnmute { return false }
        let muted = controller.snapshot?.muted ?? false
        if case .mute? = controller.activity { return !muted }
        return muted
    }

    private func accessibilityValue(volume: Int, muted: Bool) -> String {
        if case let .problem(text) = state.content { return text }
        return muted ? loc("음소거", "Muted", "ミュート") : "\(volume)"
    }

    /// Heights measured on macOS 27: 22.5 pt with one or two waves, 24.5 with three
    /// (the outer wave reaches past the body), 23.5 slashed.
    static func symbolHeight(_ symbol: String) -> CGFloat {
        switch symbol {
        case "speaker.wave.3.fill": 24.5
        case "speaker.slash.fill": 23.5
        default: 22.5
        }
    }

    /// One wave up to a third, two up to two thirds, three above, as the system's
    /// indicator shows at 6 and 25, 50, and 81 %.
    static func symbol(volume: Int, muted: Bool) -> String {
        if muted { return "speaker.slash.fill" }
        return switch volume {
        case 0: "speaker.fill"
        case ..<34: "speaker.wave.1.fill"
        case ..<67: "speaker.wave.2.fill"
        default: "speaker.wave.3.fill"
        }
    }
}
