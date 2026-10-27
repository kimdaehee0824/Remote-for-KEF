import KefRemoteCore
import ServiceManagement
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model

    @State private var hostDraft = ""
    @State private var passwordDraft = ""
    @State private var connectionError: String?

    var body: some View {
        @Bindable var model = model
        let controller = model.controller

        Form {
            Section {
                TextField("IP 주소", text: $hostDraft, prompt: Text("192.168.0.14"))
                SecureField("웹페이지 암호", text: $passwordDraft, prompt: Text(model.hasPassword ? "저장됨 (바꿀 때만 입력)" : "없음"))
                HStack {
                    if let connectionError {
                        Text(connectionError).foregroundStyle(.red)
                    } else {
                        Text(connectionText(controller.phase)).foregroundStyle(.secondary)
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
                Text("스피커")
            } footer: {
                Text("KEF 계정 암호가 아니라 스피커 웹페이지 암호입니다. 설정하지 않았다면 비워 두세요. 연결과 재연결은 상태를 읽기만 하고 음량이나 입력을 바꾸지 않습니다.")
            }

            Section {
                Stepper(value: $model.appLimit, in: 0...100) {
                    LabeledContent("앱 상한", value: "\(model.appLimit)")
                }
                if let deviceLimit = controller.device?.deviceVolumeLimit {
                    LabeledContent("기기 상한", value: "\(deviceLimit)")
                }
            } header: {
                Text("음량")
            } footer: {
                Text("이 앱에서 올릴 수 있는 최대 음량입니다. 기기 상한이 켜져 있으면 둘 중 낮은 값을 씁니다. 낮추기는 언제나 가능하며, 리모컨이나 KEF Connect의 변경은 제한하지 않습니다.")
            }

            Section("일반") {
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
                Toggle("메뉴 막대에 음량 숫자 표시", isOn: $model.showsVolumeInMenuBar)
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
        .frame(width: 460)
        .fixedSize(horizontal: false, vertical: true)
        .onAppear {
            hostDraft = model.host
            passwordDraft = ""
            model.refreshLoginItemStatus()
        }
    }

    private func connectionText(_ phase: SpeakerController.Phase) -> String {
        switch phase {
        case .online: "연결됨"
        case .connecting: "연결 중…"
        case let .offline(message): message
        case .idle: "설정되지 않음"
        }
    }
}
