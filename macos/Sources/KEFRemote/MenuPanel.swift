import AppKit
import KefRemoteCore
import SwiftUI

/// Window-style menu bar panel modeled on the Control Center Sound module.
struct MenuPanel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        let controller = model.controller
        VStack(alignment: .leading, spacing: 12) {
            HeaderView()
            content(controller)
            if let notice = controller.notice {
                Label(notice, systemImage: "info.circle")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Divider()
            HStack {
                Button("설정…") { showSettings() }
                Spacer()
                Button("종료") { NSApp.terminate(nil) }
            }
            .buttonStyle(.borderless)
        }
        .padding(14)
        .frame(width: 300)
        .onAppear {
            controller.isPanelVisible = true
            Task { await controller.refresh() }
        }
        .onDisappear { controller.isPanelVisible = false }
    }

    @ViewBuilder
    private func content(_ controller: SpeakerController) -> some View {
        switch controller.phase {
        case .online:
            if let state = controller.snapshot, let device = controller.device {
                if state.power == .standby {
                    StandbySection(device: device)
                } else {
                    VolumeSection(state: state)
                    if device.capabilities.contains(.source), !device.sources.isEmpty {
                        SourceSection(device: device, state: state)
                    }
                    if device.capabilities.contains(.power) {
                        PanelRow(title: "대기 모드로 전환", systemImage: "power", disabled: controller.isBusy) {
                            controller.standby()
                        }
                    }
                }
            }
        case .connecting:
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("스피커 상태를 읽는 중").foregroundStyle(.secondary)
            }
        case let .offline(message):
            VStack(alignment: .leading, spacing: 8) {
                Text(message)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button("다시 연결") { model.connect() }
            }
        case .idle:
            VStack(alignment: .leading, spacing: 8) {
                Text("스피커 IP 주소를 설정하면 음량과 입력을 제어할 수 있습니다.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button("스피커 설정…") { showSettings() }
            }
        }
    }

    private func showSettings() {
        NSApp.activate()
        openSettings()
    }
}

private struct HeaderView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let controller = model.controller
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 2) {
                Text(controller.device?.name ?? "Remote for KEF")
                    .font(.headline)
                    .lineLimit(1)
                Text(subtitle(controller))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer()
            Circle()
                .fill(statusColor(controller.phase))
                .frame(width: 8, height: 8)
                .help(statusText(controller.phase))
        }
    }

    private func subtitle(_ controller: SpeakerController) -> String {
        var parts: [String] = []
        if let device = controller.device {
            parts.append(device.verifiedModel ? device.model : "\(device.model) (미검증 모델)")
        }
        if !model.host.isEmpty { parts.append(model.host) }
        return parts.isEmpty ? "연결된 스피커 없음" : parts.joined(separator: " · ")
    }

    private func statusColor(_ phase: SpeakerController.Phase) -> Color {
        switch phase {
        case .online: .green
        case .connecting: .orange
        case .offline: .red
        case .idle: .secondary
        }
    }

    private func statusText(_ phase: SpeakerController.Phase) -> String {
        switch phase {
        case .online: "연결됨"
        case .connecting: "연결 중"
        case .offline: "연결 끊김"
        case .idle: "설정 필요"
        }
    }
}

/// The slider shows a draft while dragging and sends once on release.
/// Keyboard changes are merged for 200 ms before sending.
private struct VolumeSection: View {
    @Environment(AppModel.self) private var model
    let state: SpeakerSnapshot

    @State private var draft: Double?
    @State private var isEditing = false
    @State private var commitTask: Task<Void, Never>?

    var body: some View {
        let controller = model.controller
        let limit = controller.effectiveLimit
        let shown = Int((draft ?? Double(controller.pendingVolume ?? state.volume)).rounded())

        VStack(alignment: .leading, spacing: 6) {
            SectionTitle("음량")
            HStack(spacing: 8) {
                Button {
                    controller.toggleMute()
                } label: {
                    Image(systemName: state.muted ? "speaker.slash.fill" : "speaker.fill")
                        .frame(width: 18)
                }
                .buttonStyle(.borderless)
                .help(state.muted ? "음소거 해제" : "음소거")

                Slider(value: binding(controller), in: 0...100, step: 1, onEditingChanged: { editing in
                    isEditing = editing
                    if !editing { commit(controller) }
                })
                .controlSize(.small)

                Text("\(shown)")
                    .monospacedDigit()
                    .frame(width: 28, alignment: .trailing)
            }
            Group {
                if state.volume > limit {
                    Text("현재 음량이 상한(\(limit))보다 높습니다. 낮추기만 할 수 있습니다.")
                        .foregroundStyle(.orange)
                } else if state.muted {
                    Text("음소거됨 · 상한 \(limit)")
                        .foregroundStyle(.secondary)
                } else {
                    Text("상한 \(limit)")
                        .foregroundStyle(.secondary)
                }
            }
            .font(.caption)
        }
    }

    private func binding(_ controller: SpeakerController) -> Binding<Double> {
        Binding(
            get: { draft ?? Double(controller.pendingVolume ?? state.volume) },
            set: { value in
                draft = value
                if !isEditing { scheduleCommit(controller) }
            }
        )
    }

    private func scheduleCommit(_ controller: SpeakerController) {
        commitTask?.cancel()
        commitTask = Task {
            try? await Task.sleep(for: .milliseconds(200))
            guard !Task.isCancelled else { return }
            commit(controller)
        }
    }

    private func commit(_ controller: SpeakerController) {
        commitTask?.cancel()
        commitTask = nil
        guard let draft else { return }
        self.draft = nil
        controller.setVolume(Int(draft.rounded()))
    }
}

private struct SourceSection: View {
    @Environment(AppModel.self) private var model
    let device: DeviceInfo
    let state: SpeakerSnapshot

    var body: some View {
        let controller = model.controller
        VStack(alignment: .leading, spacing: 2) {
            SectionTitle("입력")
            ForEach(device.sources) { source in
                SourceRow(source: source, selected: state.sourceId == source.id, disabled: controller.isBusy) {
                    controller.selectSource(source.id)
                }
            }
        }
    }
}

private struct StandbySection: View {
    @Environment(AppModel.self) private var model
    let device: DeviceInfo

    var body: some View {
        let controller = model.controller
        VStack(alignment: .leading, spacing: 2) {
            SectionTitle(device.capabilities.contains(.power) ? "대기 중 · 켤 입력 선택" : "대기 중 · 리모컨이나 KEF Connect로 켜 주세요")
            ForEach(device.sources) { source in
                SourceRow(
                    source: source,
                    selected: controller.lastActiveSource == source.id,
                    disabled: controller.isBusy || !device.capabilities.contains(.power)
                ) {
                    controller.wake(to: source.id)
                }
            }
        }
    }
}

private struct SectionTitle: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(.secondary)
    }
}

/// Row with the circular icon and hover highlight used by system menus.
private struct SourceRow: View {
    let source: SourceOption
    let selected: Bool
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
                    .background(Circle().fill(selected ? Color.accentColor : Color.secondary.opacity(0.2)))
                Text(source.name)
                Spacer()
                if selected {
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
        .onHover { hovering = $0 }
    }
}

private struct PanelRow: View {
    let title: String
    let systemImage: String
    let disabled: Bool
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: systemImage)
                    .frame(width: 26)
                Text(title)
                Spacer()
            }
            .padding(.horizontal, 6)
            .padding(.vertical, 4)
            .contentShape(Rectangle())
            .background(RoundedRectangle(cornerRadius: 6).fill(hovering && !disabled ? Color.primary.opacity(0.08) : Color.clear))
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .onHover { hovering = $0 }
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
