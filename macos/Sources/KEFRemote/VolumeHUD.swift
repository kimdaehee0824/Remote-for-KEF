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
            // Volume up while muted also unmutes, like the Mac's own volume keys. The unmute
            // goes first; the volume waits behind it.
            if direction > 0, state.muted, !controller.isBusy { controller.toggleMute() }
            if target != current { controller.setVolume(target) }
            if direction > 0, target == current, current >= limit {
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

/// The volume indicator, drawn like the system's on macOS 26 and later (measured on
/// macOS 27): a 308 × 66 pt glass capsule 10 pt under the menu bar and 28.5 pt from
/// the right edge, the speaker symbol and a track of 16 segments, no text. It grows
/// in from 92 %, stays about a second and shrinks out, and never takes focus or clicks.
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

    /// How long the indicator stays after the last press before it starts to go.
    static let shownFor: Duration = .milliseconds(1150)
    static let inDuration = 0.25
    static let outDuration = 0.25

    private let model: AppModel
    private let state = HUDState()
    private var panel: NSPanel?
    private var hideTask: Task<Void, Never>?
    private var shownCount = 0

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
            // Drawn small and clear first, then grown in on the next turn.
            state.isShown = false
            panel.orderFrontRegardless()
            Task { [weak self] in
                guard let self else { return }
                withAnimation(.easeOut(duration: Self.inDuration)) { self.state.isShown = true }
            }
        } else if !state.isShown {
            withAnimation(.easeOut(duration: Self.inDuration)) { state.isShown = true }
        }
        hideTask?.cancel()
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: Self.shownFor)
            guard !Task.isCancelled else { return }
            self?.hide()
        }
    }

    private func hide() {
        let count = shownCount
        withAnimation(.easeIn(duration: Self.outDuration)) { state.isShown = false }
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(Self.outDuration + 0.05))
            // Unless another press brought it back meanwhile.
            guard let self, count == self.shownCount, !self.state.isShown else { return }
            self.panel?.orderOut(nil)
        }
    }

    private func makePanel() -> NSPanel {
        let size = HUDView.windowSize
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
        let host = NSHostingView(rootView: HUDWindowContent(model: model, state: state))
        host.sizingOptions = []
        host.frame = CGRect(origin: .zero, size: size)
        panel.contentView = host
        return panel
    }

    /// On the screen with the active menu bar, under it at the right, where the system's own indicator appears.
    private func place(_ panel: NSPanel) {
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let margin = HUDView.windowMargin
        let capsule = HUDView.size
        let origin = CGPoint(
            x: screen.frame.maxX - HUDView.rightInset - capsule.width - margin,
            y: screen.visibleFrame.maxY - HUDView.topInset - capsule.height - margin
        )
        panel.setFrame(CGRect(origin: origin, size: HUDView.windowSize), display: false)
    }
}

@MainActor
@Observable
final class HUDState {
    var content: VolumeHUD.Content = .level
    var isShown = false
}

/// The capsule with room around it, so growing in and the glass edge aren't clipped.
private struct HUDWindowContent: View {
    let model: AppModel
    let state: HUDState

    var body: some View {
        HUDView(model: model, state: state)
            .scaleEffect(state.isShown ? 1 : 0.92)
            .opacity(state.isShown ? 1 : 0)
            .frame(width: HUDView.windowSize.width, height: HUDView.windowSize.height)
    }
}

/// Symbol and segmented track in one glass capsule, sized and placed as measured on macOS 27.
struct HUDView: View {
    static let size = CGSize(width: 308, height: 66)
    static let rightInset: CGFloat = 28.5
    static let topInset: CGFloat = 10
    static let windowMargin: CGFloat = 20
    static var windowSize: CGSize { CGSize(width: size.width + windowMargin * 2, height: size.height + windowMargin * 2) }
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
        let content = ZStack(alignment: .leading) {
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
                SegmentedTrack(
                    volume: muted ? 0 : volume,
                    limit: controller.effectiveLimit,
                    atLimit: state.content == .limit
                )
                .frame(width: Self.trackWidth)
                .padding(.leading, Self.trackLeading)
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

        #if compiler(>=6.2)
        if #available(macOS 26, *) {
            content.glassEffect(.regular, in: Capsule())
        } else {
            content.background(.regularMaterial, in: Capsule())
        }
        #else
        content.background(.regularMaterial, in: Capsule())
        #endif
    }

    /// The mute state the speaker is heading for while a mute command runs.
    private func muteShown(_ controller: SpeakerController) -> Bool {
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
