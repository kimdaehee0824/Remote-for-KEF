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
                hud.show(.limit(current > limit
                    ? loc("상한 \(limit)보다 높아 올리지 않습니다", "Above the limit \(limit), not raised", "上限 \(limit) を超えているため上げません")
                    : loc("상한 \(limit)", "Limit \(limit)", "上限 \(limit)")))
            } else if model.showsVolumeHUD {
                hud.show(.level)
            }
        case .toggleMute:
            controller.toggleMute()
            if model.showsVolumeHUD { hud.show(.level) }
        }
    }
}

/// The volume indicator: a small glass panel like the system's, for the speaker's
/// volume. It never takes focus or clicks, and fades out a moment after the last press.
@MainActor
final class VolumeHUD {
    enum Content: Equatable {
        /// The speaker's volume, live from the controller.
        case level
        /// The volume with a line on why it didn't go higher.
        case limit(String)
        /// Why nothing changed.
        case problem(String)
    }

    static let shownFor: Duration = .milliseconds(1500)

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
            panel.alphaValue = 1
            panel.orderFrontRegardless()
        } else if panel.alphaValue < 1 {
            // Back from a fade that had started.
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.1
                panel.animator().alphaValue = 1
            }
        }
        hideTask?.cancel()
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: Self.shownFor)
            guard !Task.isCancelled else { return }
            self?.fadeOut()
        }
    }

    private func fadeOut() {
        guard let panel else { return }
        let count = shownCount
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.35
            panel.animator().alphaValue = 0
        } completionHandler: { [weak self] in
            Task { @MainActor in self?.hideIfStill(count) }
        }
    }

    /// Unless another press came during the fade.
    private func hideIfStill(_ count: Int) {
        guard count == shownCount else { return }
        panel?.orderOut(nil)
    }

    private func makePanel() -> NSPanel {
        let panel = NSPanel(
            contentRect: CGRect(x: 0, y: 0, width: HUDView.width, height: 60),
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
        panel.contentView = NSHostingView(rootView: HUDView(model: model, state: state))
        return panel
    }

    /// Top right of the screen with the menu bar, under it, where the system's own indicator appears.
    private func place(_ panel: NSPanel) {
        guard let screen = NSScreen.screens.first, let view = panel.contentView else { return }
        view.layoutSubtreeIfNeeded()
        let size = view.fittingSize
        let area = screen.visibleFrame
        let origin = CGPoint(
            x: area.maxX - size.width - HUDView.screenMargin,
            y: area.maxY - size.height - HUDView.screenMargin
        )
        panel.setFrame(CGRect(origin: origin, size: size), display: true)
    }
}

@MainActor
@Observable
final class HUDState {
    var content: VolumeHUD.Content = .level
}

/// Icon, level bar and number in one glass rounded rectangle.
struct HUDView: View {
    static let width: CGFloat = 270
    static let screenMargin: CGFloat = 10

    let model: AppModel
    let state: HUDState

    var body: some View {
        let controller = model.controller
        let snapshot = controller.snapshot
        let volume = controller.pendingVolume ?? snapshot?.volume ?? 0
        let muted = muteShown(controller)
        let content = VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline) {
                Text(controller.device?.name.nonEmpty ?? "Remote for KEF")
                    .font(.headline)
                    .lineLimit(1)
                Spacer(minLength: 8)
                if case .problem = state.content {} else {
                    Text(muted ? loc("음소거", "Muted", "ミュート") : "\(volume)")
                        .font(.headline)
                        .monospacedDigit()
                        .foregroundStyle(.secondary)
                        .contentTransition(.numericText(value: Double(volume)))
                }
            }
            switch state.content {
            case .level:
                LevelRow(volume: volume, muted: muted, limit: controller.effectiveLimit)
            case let .limit(text):
                LevelRow(volume: volume, muted: muted, limit: controller.effectiveLimit)
                Text(text).font(.caption).foregroundStyle(.secondary)
            case let .problem(text):
                Label(text, systemImage: "exclamationmark.circle")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .frame(width: Self.width, alignment: .leading)
        .animation(.snappy(duration: 0.15), value: volume)

        #if compiler(>=6.2)
        if #available(macOS 26, *) {
            content.glassEffect(.regular, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        } else {
            content.background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
        #else
        content.background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        #endif
    }

    /// The mute state the speaker is heading for while a mute command runs.
    private func muteShown(_ controller: SpeakerController) -> Bool {
        let muted = controller.snapshot?.muted ?? false
        if case .mute? = controller.activity { return !muted }
        return muted
    }
}

/// Speaker symbol and the level, with the limit marked like the panel's slider.
private struct LevelRow: View {
    let volume: Int
    let muted: Bool
    let limit: Int

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: Self.symbol(volume: volume, muted: muted))
                .font(.system(size: 15, weight: .semibold))
                .frame(width: 22, alignment: .leading)
                .contentTransition(.symbolEffect(.replace))
            GeometryReader { proxy in
                let width = proxy.size.width
                ZStack(alignment: .leading) {
                    Capsule().fill(Color.primary.opacity(0.15))
                    Capsule().fill(Color.primary.opacity(muted ? 0.35 : 0.85))
                        .frame(width: width * CGFloat(volume) / 100)
                    if limit < 100 {
                        RoundedRectangle(cornerRadius: 1)
                            .fill(Color.secondary)
                            .frame(width: 2, height: 10)
                            .offset(x: width * CGFloat(limit) / 100 - 1)
                    }
                }
                .frame(maxHeight: .infinity)
            }
            .frame(height: 10)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(loc("음량", "Volume", "音量"))
        .accessibilityValue(muted ? loc("음소거", "Muted", "ミュート") : "\(volume)")
    }

    static func symbol(volume: Int, muted: Bool) -> String {
        if muted || volume == 0 { return "speaker.slash.fill" }
        return switch volume {
        case ..<34: "speaker.wave.1.fill"
        case ..<67: "speaker.wave.2.fill"
        default: "speaker.wave.3.fill"
        }
    }
}
