import AppKit
import KefRemoteCore
import SwiftUI

/// Menu bar panel (design M1-M4). One place per fact: the switch says power,
/// the check says the input, the number says the volume. The header's second
/// line is the model unless something is out of the ordinary.
struct MenuPanel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openSettings) private var openSettings

    /// A result line stays this long, then the panel goes back to normal.
    static let noticeLifetime: Duration = .seconds(8)

    var body: some View {
        let controller = model.controller
        VStack(alignment: .leading, spacing: 14) {
            PanelHeader()
            content(controller)
            if let notice = controller.notice {
                Label(notice, systemImage: "info.circle")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            PanelFooter { showSettings(nil) }
        }
        .padding(16)
        .frame(width: 320)
        .onAppear { model.panelDidAppear() }
        .onDisappear { model.panelDidDisappear() }
        .task(id: controller.notice) {
            guard controller.notice != nil else { return }
            try? await Task.sleep(for: Self.noticeLifetime)
            if !Task.isCancelled { controller.clearNotice() }
        }
    }

    @ViewBuilder
    private func content(_ controller: SpeakerController) -> some View {
        switch controller.phase {
        case .idle:
            PanelMessage(
                title: "스피커를 연결하세요",
                text: "설정에서 스피커의 IP 주소를 입력하면 여기서 바로 음량과 입력을 바꿀 수 있습니다."
            ) {
                Button("스피커 설정…") { showSettings(.speaker) }
            }
        case .offline:
            PanelMessage(
                title: "스피커가 응답하지 않습니다",
                text: "스피커 전원과 Wi-Fi 연결을 확인하세요. 같은 네트워크인데도 안 되면 시스템 설정에서 로컬 네트워크 권한을 확인하세요. 잠시 뒤 자동으로 다시 시도합니다."
            ) {
                Button("다시 연결") { model.connect() }
                Button("스피커 설정…") { showSettings(.speaker) }
            }
        case .connecting, .online:
            if let state = controller.snapshot, let device = controller.device {
                if state.power == .standby {
                    StandbySection(device: device)
                } else {
                    VolumeSection(state: state)
                    if device.capabilities.contains(.source), device.sources.count > 1 {
                        SourceSection(device: device, state: state)
                    }
                }
            } else {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("스피커 상태를 읽는 중").foregroundStyle(.secondary)
                }
            }
        }
    }

    private func showSettings(_ tab: SettingsTab?) {
        model.openSettings(tab, using: openSettings)
    }
}

// MARK: Header

/// Name and model, with the power switch where Control Center puts the Wi-Fi
/// and Bluetooth switches (design M1, 2).
private struct PanelHeader: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let controller = model.controller
        let isOn = controller.snapshot?.power == .on && controller.phase != .idle
        HStack(spacing: 10) {
            Image(nsImage: MenuBarGlyph.on.image)
                .renderingMode(.template)
                .foregroundStyle(isOn ? Color.white : Color.secondary)
                .frame(width: 30, height: 30)
                .background(Circle().fill(isOn ? Color.accentColor : Color.secondary.opacity(0.18)))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text(controller.device?.name.nonEmpty ?? "Remote for KEF")
                    .font(.headline)
                    .lineLimit(1)
                Text(subtitle(controller))
                    .font(.subheadline)
                    .foregroundStyle(subtitleIsError(controller) ? Color.red : Color.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            Toggle("전원", isOn: powerBinding(controller))
                .toggleStyle(.switch)
                .labelsHidden()
                .disabled(!canTogglePower(controller))
                .help(controller.snapshot?.power == .on ? "대기 상태로 끄기" : "마지막 입력으로 켜기")
        }
    }

    private func subtitle(_ c: SpeakerController) -> String {
        switch c.activity {
        case let .source(id)?:
            let name = SourceNames.short(c.device?.sources.first { $0.id == id }?.name ?? id)
            return "\(name)\(Korean.ro(name)) 바꾸는 중"
        case .powerOn?:
            return "켜는 중"
        case .powerOff?:
            return "끄는 중"
        default:
            break
        }
        switch c.phase {
        case .idle:
            return "스피커를 설정하세요"
        case .connecting:
            return c.snapshot == nil ? "연결하는 중" : (c.device?.model ?? "")
        case .offline:
            return "연결 끊김"
        case .online:
            guard let device = c.device else { return "" }
            if c.snapshot?.power == .standby { return "대기" }
            return device.verifiedModel ? device.model : "\(device.model) (미검증 모델)"
        }
    }

    private func subtitleIsError(_ c: SpeakerController) -> Bool {
        if case .offline = c.phase { return c.activity == nil }
        return false
    }

    /// Shows the requested state while the switch is being verified.
    private func powerBinding(_ c: SpeakerController) -> Binding<Bool> {
        Binding(
            get: {
                switch c.activity {
                case .powerOn?: true
                case .powerOff?: false
                default: c.snapshot?.power == .on && c.phase != .idle
                }
            },
            set: { on in
                if on { c.wake() } else { c.standby() }
            }
        )
    }

    private func canTogglePower(_ c: SpeakerController) -> Bool {
        c.isOnline && !c.isBusy && c.snapshot != nil && c.device?.capabilities.contains(.power) == true
    }
}

// MARK: Volume

/// System slider with the limit marked on it. Dragging sends as it goes (the
/// controller keeps only the latest target), taps the trackpad every 5 and
/// once more, differently, at the limit (design M1, 3).
private struct VolumeSection: View {
    @Environment(AppModel.self) private var model
    let state: SpeakerSnapshot

    @State private var draft: Int?
    @State private var isDragging = false

    var body: some View {
        let controller = model.controller
        let limit = controller.effectiveLimit
        let shown = draft ?? controller.pendingVolume ?? state.volume
        let switching = controller.activity.switchingSource != nil

        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text("음량").font(.headline)
                Spacer()
                Text("\(shown)")
                    .font(.title2.weight(.semibold))
                    .monospacedDigit()
                    .contentTransition(.numericText(value: Double(shown)))
                    .accessibilityHidden(true)
            }
            HStack(spacing: 8) {
                Button {
                    controller.toggleMute()
                } label: {
                    Image(systemName: state.muted ? "speaker.slash.fill" : "speaker.fill")
                        .frame(width: 18)
                }
                .buttonStyle(.borderless)
                .help(state.muted ? "음소거 해제" : "음소거")
                .accessibilityLabel(state.muted ? "음소거 해제" : "음소거")

                Slider(value: binding(controller, limit: limit), in: 0...100, step: 1) {
                    Text("음량")
                } onEditingChanged: { editing in
                    isDragging = editing
                    if !editing { draft = nil }
                }
                .labelsHidden()
                .background(LimitMark(limit: limit))
                .accessibilityValue("\(shown), 상한 \(limit)")
            }
            HStack {
                if state.volume > limit {
                    Text("현재 음량이 상한(\(limit))보다 높습니다. 낮추기만 할 수 있습니다.")
                        .foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Text("상한 \(limit) (\(controller.limitFromDevice ? "KEF 기기 설정" : "앱 설정"))")
                        .foregroundStyle(.secondary)
                    Spacer()
                    if switching {
                        Text("바꾸는 동안 잠시 음소거").foregroundStyle(.secondary)
                    }
                }
            }
            .font(.caption)
        }
        .disabled(switching)
        .sensoryFeedback(.levelChange, trigger: shown / 5) { _, _ in isDragging }
    }

    /// Raising stops at the limit (or at the current volume when that is already
    /// above it); lowering always works. Same rule as the controller.
    private func binding(_ controller: SpeakerController, limit: Int) -> Binding<Double> {
        Binding(
            get: { Double(draft ?? controller.pendingVolume ?? state.volume) },
            set: { value in
                let requested = Int(value.rounded())
                let current = controller.snapshot?.volume ?? state.volume
                let allowed = VolumePolicy.target(requested: requested, current: current, limit: limit)
                let previous = draft ?? controller.pendingVolume ?? current
                if isDragging, allowed == limit, requested > limit, previous < limit {
                    NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
                }
                if isDragging { draft = allowed }
                if allowed != previous { controller.setVolume(allowed) }
            }
        )
    }
}

/// Orange tick at the limit with the range above it hatched, drawn behind the
/// system slider's track.
private struct LimitMark: View {
    let limit: Int
    /// Half the knob width: where the track's 0 and 100 sit inside the control.
    static let trackInset: CGFloat = 10

    var body: some View {
        GeometryReader { proxy in
            let inset = Self.trackInset
            let usable = max(0, proxy.size.width - inset * 2)
            let x = inset + usable * CGFloat(limit) / 100
            let mid = proxy.size.height / 2
            if limit < 100 {
                ZStack(alignment: .topLeading) {
                    Hatching()
                        .stroke(Color.orange.opacity(0.45), lineWidth: 1)
                        .frame(width: max(0, proxy.size.width - inset - x), height: 10)
                        .clipShape(RoundedRectangle(cornerRadius: 2))
                        .offset(x: x, y: mid - 5)
                    RoundedRectangle(cornerRadius: 1)
                        .fill(Color.orange)
                        .frame(width: 2, height: 14)
                        .offset(x: x - 1, y: mid - 7)
                }
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

private struct Hatching: Shape {
    func path(in rect: CGRect) -> Path {
        var path = Path()
        var x = rect.minX - rect.height
        while x < rect.maxX {
            path.move(to: CGPoint(x: x, y: rect.maxY))
            path.addLine(to: CGPoint(x: x + rect.height, y: rect.minY))
            x += 4
        }
        return path
    }
}

// MARK: Inputs

private struct SourceSection: View {
    @Environment(AppModel.self) private var model
    let device: DeviceInfo
    let state: SpeakerSnapshot

    var body: some View {
        let controller = model.controller
        let switchingTo = controller.activity.switchingSource
        VStack(alignment: .leading, spacing: 2) {
            Text("입력").font(.headline).padding(.bottom, 4)
            ForEach(device.sources) { source in
                SourceRow(
                    source: source,
                    selected: (switchingTo ?? state.sourceId) == source.id,
                    inProgress: switchingTo == source.id,
                    dimmed: switchingTo != nil && switchingTo != source.id,
                    disabled: controller.isBusy
                ) {
                    controller.selectSource(source.id)
                }
            }
            if switchingTo != nil {
                Text("전환이 끝나면 음량을 확인하고 음소거를 풉니다. 새 입력이 더 크면 음소거를 유지합니다.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 4)
            }
        }
    }
}

/// Standby: the switch wakes on the last input, a row wakes on that input (design M3).
private struct StandbySection: View {
    @Environment(AppModel.self) private var model
    let device: DeviceInfo

    var body: some View {
        let controller = model.controller
        let canPower = device.capabilities.contains(.power)
        let wakingTo = controller.activity.wakingSource(last: controller.lastActiveSource)
        VStack(alignment: .leading, spacing: 2) {
            if device.capabilities.contains(.source), device.sources.count > 1 {
                Text("이 입력으로 켜기").font(.headline).padding(.bottom, 4)
                ForEach(device.sources) { source in
                    SourceRow(
                        source: source,
                        selected: false,
                        detail: controller.lastActiveSource == source.id ? "마지막 입력" : nil,
                        inProgress: wakingTo == source.id,
                        dimmed: wakingTo != nil && wakingTo != source.id,
                        disabled: controller.isBusy || !canPower
                    ) {
                        controller.wake(to: source.id)
                    }
                }
            }
            Text(canPower
                ? "켜기 전에 음량이 상한 이하인지 확인합니다. 대기 전 음량이 상한보다 높으면 켜지 않고 이유를 알려 드립니다."
                : "이 기기는 앱에서 켤 수 없습니다. 리모컨이나 KEF Connect로 켜 주세요.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 4)
        }
    }
}

/// Circular icon, name and check, like the output list in the Sound menu. A
/// spinner replaces the check on the row being switched to, as in the Wi-Fi menu.
private struct SourceRow: View {
    let source: SourceOption
    let selected: Bool
    var detail: String?
    let inProgress: Bool
    let dimmed: Bool
    let disabled: Bool
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: SourceIcon.symbol(for: source.id))
                    .font(.system(size: 12, weight: .medium))
                    .frame(width: 26, height: 26)
                    .foregroundStyle(selected ? Color.white : Color.primary)
                    .background(Circle().fill(selected ? Color.accentColor : Color.secondary.opacity(0.18)))
                Text(source.name)
                Spacer()
                if let detail {
                    Text(detail).font(.caption).foregroundStyle(.secondary)
                }
                if inProgress {
                    ProgressView().controlSize(.small)
                } else if selected {
                    Image(systemName: "checkmark").foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 6)
            .padding(.vertical, 3)
            .contentShape(Rectangle())
            .background(RoundedRectangle(cornerRadius: 6).fill(hovering && !disabled ? Color.primary.opacity(0.08) : Color.clear))
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .opacity(dimmed ? 0.45 : 1)
        .onHover { hovering = $0 }
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

// MARK: Messages and footer

/// One line on what happened and one on what to do (design M4).
private struct PanelMessage<Actions: View>: View {
    let title: String
    let text: String
    @ViewBuilder let actions: Actions

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.headline)
            Text(text)
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) { actions }
                .padding(.top, 4)
        }
    }
}

/// Settings and Quit as the system glass buttons (design M1, 5). A menu bar
/// app has no app menu, so the buttons carry ⌘, and ⌘Q themselves.
private struct PanelFooter: View {
    let openSettings: () -> Void

    var body: some View {
        let buttons = HStack(spacing: 8) {
            Button(action: openSettings) {
                Text("설정").frame(maxWidth: .infinity)
            }
            .keyboardShortcut(",", modifiers: .command)
            .help("설정 (⌘,)")
            Button {
                NSApp.terminate(nil)
            } label: {
                Text("종료").frame(maxWidth: .infinity)
            }
            .keyboardShortcut("q", modifiers: .command)
            .help("Remote for KEF 종료 (⌘Q)")
        }
        .controlSize(.large)

        #if compiler(>=6.2)
        if #available(macOS 26, *) {
            GlassEffectContainer {
                buttons.buttonStyle(.glass)
            }
        } else {
            buttons.buttonStyle(.bordered)
        }
        #else
        buttons.buttonStyle(.bordered)
        #endif
    }
}

// MARK: Names and icons

enum SourceNames {
    /// "USB-C / PC" becomes "USB-C", "TV (HDMI eARC)" becomes "TV".
    static func short(_ name: String) -> String {
        var result = name
        for separator in [" / ", " ("] {
            if let range = result.range(of: separator) { result = String(result[..<range.lowerBound]) }
        }
        return result
    }
}

enum SourceIcon {
    static func symbol(for sourceId: String) -> String {
        switch sourceId {
        case "usb": "cable.connector"
        case "wifi": "wifi"
        case "bluetooth": "dot.radiowaves.left.and.right"
        case "tv": "tv"
        case "optical": "rays"
        case "coaxial": "cable.coaxial"
        case "analog": "waveform"
        default: "hifispeaker"
        }
    }
}

extension String {
    var nonEmpty: String? { isEmpty ? nil : self }
}

private extension Optional where Wrapped == SpeakerController.Activity {
    /// The input a running switch goes to.
    var switchingSource: String? {
        if case let .source(id)? = self { return id }
        return nil
    }

    /// The input a running wake goes to; waking without one goes to the last input.
    func wakingSource(last: String?) -> String? {
        if case let .powerOn(id)? = self { return id ?? last }
        return nil
    }
}
