import KefRemoteCore
import ServiceManagement
import SwiftUI

/// Settings window with toolbar tabs (design M5): 일반, 스피커, 음량.
struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        TabView(selection: $model.settingsTab) {
            GeneralSettings()
                .tabItem { Label("일반", systemImage: "gearshape") }
                .tag(SettingsTab.general)
            SpeakerSettings()
                .tabItem { Label("스피커", systemImage: "hifispeaker") }
                .tag(SettingsTab.speaker)
            VolumeSettings()
                .tabItem { Label("음량", systemImage: "speaker.wave.2") }
                .tag(SettingsTab.volume)
        }
    }
}

struct GeneralSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                Toggle("로그인 시 실행", isOn: Binding(
                    get: { model.loginItemStatus == .enabled || model.loginItemStatus == .requiresApproval },
                    set: { model.setLaunchAtLogin($0) }
                ))
                if model.loginItemStatus == .requiresApproval {
                    HStack {
                        Text("시스템 설정에서 허용이 필요합니다").foregroundStyle(.secondary)
                        Spacer()
                        Button("로그인 항목 열기") { SMAppService.openSystemSettingsLoginItems() }
                    }
                }
                if let error = model.loginItemError {
                    Text(error).foregroundStyle(.red)
                }
            }
            Section {
                Toggle(isOn: $model.showsVolumeInMenuBar) {
                    Text("메뉴 막대에 음량 숫자 표시")
                    Text("아이콘은 켜짐, 음소거, 대기, 연결 끊김을 모양으로 구분합니다")
                }
            } header: {
                Text("메뉴 막대")
            }
        }
        .formStyle(.grouped)
        .frame(width: 500, height: 250)
        .onAppear { model.refreshLoginItemStatus() }
    }
}

struct SpeakerSettings: View {
    @Environment(AppModel.self) private var model

    @State private var hostDraft = ""
    @State private var passwordDraft = ""
    @State private var connectionError: String?

    var body: some View {
        let controller = model.controller
        Form {
            Section {
                TextField("IP 주소", text: $hostDraft, prompt: Text("192.168.0.14"))
                SecureField("웹페이지 암호", text: $passwordDraft, prompt: Text(model.hasPassword ? "저장됨 (바꿀 때만 입력)" : "없음"))
                HStack(spacing: 8) {
                    if let connectionError {
                        StatusDot(color: .red)
                        Text(connectionError).foregroundStyle(.red)
                    } else {
                        StatusDot(color: statusColor(controller.phase))
                        Text(statusText(controller.phase))
                            .foregroundStyle(.secondary)
                            .lineLimit(2)
                    }
                    Spacer()
                    if model.hasPassword {
                        Button("암호 삭제") {
                            connectionError = model.saveConnection(host: hostDraft, password: "")
                        }
                    }
                    Button("저장하고 연결") {
                        connectionError = model.saveConnection(host: hostDraft, password: passwordDraft.isEmpty ? nil : passwordDraft)
                        passwordDraft = ""
                    }
                    .keyboardShortcut(.defaultAction)
                }
            } header: {
                Text("연결")
            } footer: {
                Text("KEF 계정 암호가 아니라 스피커 웹페이지 암호입니다. 설정하지 않았다면 비워 두세요. 연결과 재연결은 상태를 읽기만 하고 음량이나 입력을 바꾸지 않습니다. IP 주소는 KEF Connect의 스피커 정보나 공유기 관리 화면에서 확인할 수 있습니다.")
            }

            if let device = controller.device {
                Section("기기 정보") {
                    LabeledContent("이름", value: device.name)
                    LabeledContent("모델", value: device.model)
                    LabeledContent("펌웨어", value: device.firmware)
                    LabeledContent("검증 상태", value: device.verifiedModel ? "실기 검증됨" : "미검증 모델 (공개 코드 기준)")
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 500, height: controller.device == nil ? 300 : 470)
        .onAppear {
            hostDraft = model.host
            passwordDraft = ""
        }
    }

    private func statusColor(_ phase: SpeakerController.Phase) -> Color {
        switch phase {
        case .online: .green
        case .connecting: .yellow
        case .offline: .red
        case .idle: .secondary
        }
    }

    /// The full error stays here; the panel only says what to do (design M4).
    private func statusText(_ phase: SpeakerController.Phase) -> String {
        switch phase {
        case .online: "연결됨"
        case .connecting: "연결 중…"
        case let .offline(message): message
        case .idle: "설정되지 않음"
        }
    }
}

private struct StatusDot: View {
    let color: Color

    var body: some View {
        Circle().fill(color).frame(width: 8, height: 8).accessibilityHidden(true)
    }
}

struct VolumeSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        let controller = model.controller
        Form {
            Section {
                LabeledContent {
                    HStack(spacing: 8) {
                        Slider(value: Binding(
                            get: { Double(model.appLimit) },
                            set: { model.appLimit = Int($0.rounded()) }
                        ), in: 1...100, step: 1)
                        .labelsHidden()
                        .frame(width: 150)
                        TextField("앱 상한", value: $model.appLimit, format: .number)
                            .labelsHidden()
                            .multilineTextAlignment(.trailing)
                            .frame(width: 44)
                    }
                } label: {
                    Text("앱 상한")
                    Text("이 앱에서 올릴 수 있는 최대 음량")
                }
                LabeledContent {
                    Text(deviceLimitText(controller))
                } label: {
                    Text("KEF 기기 최대 음량")
                    Text("KEF Connect에서 정한 값. 둘 중 낮은 값을 씁니다")
                }
                LabeledContent("지금 적용 중인 상한") {
                    Text("\(controller.effectiveLimit) (\(controller.limitFromDevice ? "KEF 기기 설정" : "앱 설정"))")
                        .monospacedDigit()
                }
            } header: {
                Text("상한")
            } footer: {
                Text("낮추기는 언제나 됩니다. 리모컨과 KEF Connect에서 바꾸는 음량은 제한하지 않습니다.")
            }

            Section("음량 보정") {
                // Laid out by hand so the (i) button can sit right after the title.
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 4) {
                            Text("정확한 음량 맞추기")
                            ApproachInfoButton()
                        }
                        Text("올릴 때 목표보다 살짝 높게 보낸 뒤 목표로 내립니다")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Toggle("정확한 음량 맞추기", isOn: $model.approachFromAbove)
                        .toggleStyle(.switch)
                        .labelsHidden()
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 500, height: 400)
        .task { await model.refreshDeviceIfStale() }
    }

    private func deviceLimitText(_ controller: SpeakerController) -> String {
        guard let device = controller.device else { return "연결 후 표시" }
        return device.deviceVolumeLimit.map(String.init) ?? "꺼짐"
    }
}

/// The (i) button: the row's own line says what it does, the popover says how (design M5, 9).
private struct ApproachInfoButton: View {
    @State private var isPresented = false

    var body: some View {
        Button {
            isPresented.toggle()
        } label: {
            Image(systemName: "info.circle")
        }
        .buttonStyle(.borderless)
        .help("KEF 스피커는 같은 숫자라도 아래에서 올라올 때 더 작게 들리는 구간이 있습니다.")
        .accessibilityLabel("정확한 음량 맞추기 설명")
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 8) {
                Text("정확한 음량 맞추기").font(.headline)
                Text("KEF 스피커는 같은 숫자라도 아래에서 올라올 때 더 작게 들리는 구간이 있습니다. 켜면 음량을 올릴 때 목표보다 조금 높은 값을 먼저 보내고 0.1초 뒤 목표 값을 보내, 위에서 내려와 맞춘 것과 같은 크기로 들리게 합니다.")
                Text("예: 20으로 올리면 22를 거쳐 20, 12로 올리면 17을 거쳐 12가 됩니다. 5 이하와 41 이상은 바로 보냅니다. 잠깐 거치는 값도 상한을 넘지 않고, 내릴 때는 그대로 보냅니다.")
                    .foregroundStyle(.secondary)
                Text("LSX II(USB 입력)에서 잰 표를 쓰며 LSX II LT에서는 아직 확인하지 않아 기본으로 꺼 둡니다.")
                    .foregroundStyle(.secondary)
            }
            .font(.callout)
            .padding(14)
            .frame(width: 320)
            .fixedSize(horizontal: false, vertical: true)
        }
    }
}
