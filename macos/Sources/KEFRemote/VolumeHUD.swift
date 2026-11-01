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
            if direction > 0, !state.muted, target == current, current >= limit {
                hud.show(.limit)
            } else if model.showsVolumeHUD {
                hud.show(.level)
            }
        case .toggleMute:
            controller.toggleMute()
            if model.showsVolumeHUD { hud.show(.level) }
        }
    }
}

/// The volume indicator, built like the system's on macOS 26 and later and measured
/// on macOS 27: a 308 × 66 pt capsule of the system's glass (`NSGlassEffectView`)
/// 10 pt under the menu bar and 28.5 pt from the right edge, holding the speaker symbol
/// and a track of 16 segments. With the pointer over it, the track becomes a standard
/// slider that can be dragged, and the indicator stays until the pointer leaves, as
/// the system's does. It grows in from 92 %, stays about a second and shrinks out,
/// and never takes focus.
///
/// Two things are added for the speaker: the current segment fills in part, so a
/// press of 1 still shows, and a tick marks the limit. The number is in the menu bar
/// next to the icon, where the system shows its own volume pill while the indicator is up.
@MainActor
final class VolumeHUD {
    enum Content: Equatable {
        /// The speaker's volume, live from the controller.
        case level
        /// The volume is at the limit (or above it), so up did nothing.
        case limit
        /// Why nothing changed, in place of the track.
        case problem(String)
    }

    /// How long the indicator stays after the last press, or after the pointer leaves it.
    static let shownFor: Duration = .milliseconds(1150)
    static let inDuration = 0.2
    static let outDuration = 0.25
    /// The size it grows from and shrinks to.
    static let startScale: CGFloat = 0.92

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
        guard let frame = Self.frame() else { return }
        let panel = panel ?? makePanel()
        self.panel = panel
        shownCount += 1
        if !panel.isVisible {
            panel.setFrame(Self.scaled(frame, Self.startScale), display: false)
            panel.alphaValue = 0
            panel.orderFrontRegardless()
        }
        if hiding || panel.alphaValue < 1 || panel.frame != frame {
            hiding = false
            // The window grows and fades as a whole, so the glass inside draws as it does at rest.
            NSAnimationContext.runAnimationGroup { context in
                context.duration = Self.inDuration
                context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                panel.animator().setFrame(frame, display: true)
                panel.animator().alphaValue = 1
            }
        }
        scheduleHide()
    }

    private func scheduleHide() {
        hideTask?.cancel()
        guard !state.hovering else { return }
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: Self.shownFor)
            guard !Task.isCancelled else { return }
            self?.hide()
        }
    }

    private func hoverChanged(_ inside: Bool) {
        guard let panel, panel.isVisible else { return }
        state.hovering = inside
        if inside {
            hideTask?.cancel()
            if hiding { show(state.content) }
        } else {
            scheduleHide()
        }
    }

    private func hide() {
        guard let panel, let frame = Self.frame() else { return }
        let count = shownCount
        hiding = true
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Self.outDuration
            context.timingFunction = CAMediaTimingFunction(name: .easeIn)
            panel.animator().setFrame(Self.scaled(frame, Self.startScale), display: true)
            panel.animator().alphaValue = 0
        }
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(Self.outDuration + 0.05))
            // Unless another press or the pointer brought it back meanwhile.
            guard let self, count == self.shownCount, self.hiding else { return }
            self.hiding = false
            self.state.hovering = false
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
        panel.becomesKeyOnlyIfNeeded = true
        panel.level = .statusBar
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        let host = HUDHostingView(rootView: HUDView(model: model, state: state))
        host.sizingOptions = []
        host.frame = CGRect(origin: .zero, size: size)
        host.autoresizingMask = [.width, .height]
        let tracking = HoverTrackingView(frame: host.frame)
        tracking.autoresizingMask = [.width, .height]
        tracking.addSubview(host)
        tracking.onHover = { [weak self] inside in self?.hoverChanged(inside) }
        panel.contentView = Self.capsule(holding: tracking, size: size)
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
    private static func frame() -> CGRect? {
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return nil }
        let size = HUDView.size
        let origin = CGPoint(
            x: screen.frame.maxX - HUDView.rightInset - size.width,
            y: screen.visibleFrame.maxY - HUDView.topInset - size.height
        )
        return CGRect(origin: origin, size: size)
    }

    private static func scaled(_ frame: CGRect, _ scale: CGFloat) -> CGRect {
        frame.insetBy(dx: frame.width * (1 - scale) / 2, dy: frame.height * (1 - scale) / 2)
    }
}

/// Takes the first click, since the panel never becomes key.
private final class HUDHostingView: NSHostingView<HUDView> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// Reports the pointer entering and leaving the indicator, whichever app is active.
private final class HoverTrackingView: NSView {
    var onHover: (@MainActor (Bool) -> Void)?
    private var hoverArea: NSTrackingArea?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard hoverArea == nil, window != nil else { return }
        // In the visible rect, so it follows the window as it grows and shrinks.
        let area = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
        hoverArea = area
    }

    override func mouseEntered(with event: NSEvent) { onHover?(true) }
    override func mouseExited(with event: NSEvent) { onHover?(false) }
}

@MainActor
@Observable
final class HUDState {
    var content: VolumeHUD.Content = .level
    /// The pointer is over the indicator: the track is a slider and the indicator stays.
    var hovering = false
}

/// What sits on the glass: the speaker symbol and the segmented track, or a standard
/// slider while the pointer is over it, placed as measured on macOS 27.
struct HUDView: View {
    static let size = CGSize(width: 308, height: 66)
    static let rightInset: CGFloat = 28.5
    static let topInset: CGFloat = 10
    /// The speaker's body starts here; the slashed symbol 2.5 pt earlier.
    static let symbolInset: CGFloat = 20.5
    static let trackLeading: CGFloat = 62.5
    static let trackWidth: CGFloat = 214

    let model: AppModel
    let state: HUDState

    var body: some View {
        let controller = model.controller
        let volume = controller.pendingVolume ?? controller.snapshot?.volume ?? 0
        let muted = muteShown(controller)
        ZStack(alignment: .leading) {
            switch state.content {
            case .level, .limit:
                let symbol = Self.symbol(volume: volume, muted: muted)
                Image(systemName: symbol)
                    .resizable()
                    .fontWeight(.semibold)
                    .aspectRatio(contentMode: .fit)
                    .frame(height: Self.symbolHeight(symbol))
                    .foregroundStyle(.primary)
                    .padding(.leading, muted ? Self.symbolInset - 2.5 : Self.symbolInset)
                ZStack {
                    if state.hovering {
                        Slider(value: sliderValue(controller, volume: volume, muted: muted), in: 0...100)
                            .labelsHidden()
                            .tint(.primary)
                    } else {
                        SegmentedTrack(
                            volume: muted ? 0 : volume,
                            limit: controller.effectiveLimit,
                            atLimit: state.content == .limit
                        )
                    }
                }
                .frame(width: Self.trackWidth)
                .padding(.leading, Self.trackLeading)
                .animation(.easeOut(duration: 0.15), value: state.hovering)
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
                    .frame(width: Self.trackWidth, alignment: .leading)
                    .padding(.leading, Self.trackLeading)
            }
        }
        .frame(width: Self.size.width, height: Self.size.height, alignment: .leading)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(loc("음량", "Volume", "音量"))
        .accessibilityValue(accessibilityValue(volume: volume, muted: muted))
    }

    /// Dragging sends the volume like the panel's slider: never above the limit, and
    /// only lower while the speaker is already above it. Raising from mute unmutes.
    private func sliderValue(_ controller: SpeakerController, volume: Int, muted: Bool) -> Binding<Double> {
        Binding(
            get: { Double(muted ? 0 : volume) },
            set: { value in
                let limit = controller.effectiveLimit
                let target = min(Int(value.rounded()), max(limit, volume))
                if muted {
                    if target > 0 { controller.setVolumeUnmuting(target) }
                } else if target != volume {
                    controller.setVolume(target)
                }
            }
        )
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

/// 16 segments of 12.5 pt in a 214 × 4 pt track with rounded ends, filled in the
/// primary color over 14 % of it, like the system's. The segment the volume falls in
/// fills in part. The limit is a short tick, stronger when up was refused there.
private struct SegmentedTrack: View {
    static let segments = 16
    static let height: CGFloat = 4
    static let gap: CGFloat = 0.875

    let volume: Int
    let limit: Int
    let atLimit: Bool

    var body: some View {
        GeometryReader { proxy in
            let width = proxy.size.width
            let segment = (width - Self.gap * CGFloat(Self.segments - 1)) / CGFloat(Self.segments)
            let filled = CGFloat(min(100, max(0, volume))) / 100 * CGFloat(Self.segments)
            ZStack(alignment: .leading) {
                HStack(spacing: Self.gap) {
                    ForEach(0..<Self.segments, id: \.self) { index in
                        let part = min(1, max(0, filled - CGFloat(index)))
                        ZStack(alignment: .leading) {
                            Rectangle().fill(Color.primary.opacity(0.14))
                            Rectangle().fill(Color.primary)
                                .frame(width: segment * part)
                        }
                    }
                }
                .frame(height: Self.height)
                .clipShape(Capsule())
                if limit < 100 {
                    Capsule()
                        .fill(Color.primary.opacity(atLimit ? 1 : 0.45))
                        .frame(width: 1.5, height: atLimit ? 12 : 10)
                        .offset(x: width * CGFloat(limit) / 100 - 0.75)
                }
            }
            .frame(height: proxy.size.height)
            .animation(.snappy(duration: 0.12), value: volume)
            .animation(.snappy(duration: 0.15), value: atLimit)
        }
        .frame(height: 12)
    }
}
