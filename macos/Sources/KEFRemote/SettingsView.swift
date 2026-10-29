import KefRemoteCore
import KefW2Driver
import ServiceManagement
import SwiftUI

/// Settings window with toolbar tabs (design M5): General, Speaker, Volume.
struct SettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        TabView(selection: $model.settingsTab) {
            GeneralSettings()
                .tabItem { Label(loc("일반", "General", "一般"), systemImage: "gearshape") }
                .tag(SettingsTab.general)
            SpeakerSettings()
                .tabItem { Label(loc("스피커", "Speaker", "スピーカー"), systemImage: "hifispeaker") }
                .tag(SettingsTab.speaker)
            VolumeSettings()
                .tabItem { Label(loc("음량", "Volume", "音量"), systemImage: "speaker.wave.2") }
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
                Toggle(loc("로그인 시 실행", "Open at login", "ログイン時に起動"), isOn: Binding(
                    get: { model.loginItemStatus == .enabled || model.loginItemStatus == .requiresApproval },
                    set: { model.setLaunchAtLogin($0) }
                ))
                if model.loginItemStatus == .requiresApproval {
                    HStack {
                        Text(loc("시스템 설정에서 허용이 필요합니다", "Needs approval in System Settings", "システム設定で許可が必要です")).foregroundStyle(.secondary)
                        Spacer()
                        Button(loc("로그인 항목 열기", "Open Login Items", "ログイン項目を開く")) { SMAppService.openSystemSettingsLoginItems() }
                    }
                }
                if let error = model.loginItemError {
                    Text(error).foregroundStyle(.red)
                }
            }
            Section {
                Toggle(isOn: $model.showsVolumeInMenuBar) {
                    Text(loc("메뉴 막대에 음량 숫자 표시", "Show the volume in the menu bar", "メニューバーに音量を表示"))
                    Text(loc("아이콘은 켜짐, 음소거, 대기, 연결 끊김을 모양으로 구분합니다", "The icon's shape tells on, muted, standby and offline apart", "アイコンの形でオン、ミュート、スタンバイ、オフラインを区別します"))
                }
            } header: {
                Text(loc("메뉴 막대", "Menu Bar", "メニューバー"))
            }
            UpdateSettings()
        }
        .formStyle(.grouped)
        .frame(width: 500, height: 440)
        .onAppear { model.refreshLoginItemStatus() }
    }
}

/// Sparkle's settings (kept in the app's defaults) and a manual check that opens Sparkle's window.
private struct UpdateSettings: View {
    @Bindable private var updater = AppUpdater.shared

    var body: some View {
        Section {
            LabeledContent(loc("현재 버전", "Current version", "現在のバージョン"), value: updater.currentVersion)
            if updater.isConfigured {
                Toggle(loc("자동으로 업데이트 확인", "Check for updates automatically", "アップデートを自動で確認"), isOn: $updater.checksAutomatically)
                Toggle(isOn: $updater.downloadsAutomatically) {
                    Text(loc("자동으로 내려받아 설치", "Download and install automatically", "自動でダウンロードしてインストール"))
                    Text(loc("하루에 한 번 GitHub에서 확인하고, 앱을 종료할 때 설치합니다", "Checks GitHub once a day and installs when the app quits", "1日に1回GitHubで確認し、アプリの終了時にインストールします"))
                }
                .disabled(!updater.checksAutomatically)
                HStack {
                    Spacer()
                    Button(loc("지금 확인…", "Check Now…", "今すぐ確認…")) { updater.checkForUpdates() }
                }
            } else {
                Text(loc("이 빌드는 스스로 업데이트하지 않습니다. GitHub 릴리스에서 받은 앱만 업데이트합니다.", "This build doesn't update itself. Only the app from the GitHub releases does.", "このビルドは自動でアップデートしません。GitHubのリリースから入手したアプリだけがアップデートします。"))
                    .foregroundStyle(.secondary)
            }
        } header: {
            Text(loc("업데이트", "Updates", "アップデート"))
        }
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
                TextField(loc("IP 주소", "IP address", "IPアドレス"), text: $hostDraft, prompt: Text("192.168.0.14"))
                SecureField(loc("웹페이지 암호", "Web page password", "Webページのパスワード"), text: $passwordDraft, prompt: Text(model.hasPassword ? loc("저장됨 (바꿀 때만 입력)", "Saved (enter only to change it)", "保存済み（変更する場合のみ入力）") : loc("없음", "None", "なし")))
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
                        Button(loc("암호 삭제", "Delete Password", "パスワードを削除")) {
                            connectionError = model.saveConnection(host: hostDraft, password: "")
                        }
                    }
                    Button(loc("저장하고 연결", "Save and Connect", "保存して接続")) {
                        connectionError = model.saveConnection(host: hostDraft, password: passwordDraft.isEmpty ? nil : passwordDraft)
                        passwordDraft = ""
                    }
                    .keyboardShortcut(.defaultAction)
                }
            } header: {
                Text(loc("연결", "Connection", "接続"))
            } footer: {
                Text(wrapping: loc("KEF 계정 암호가 아니라 스피커 웹페이지 암호입니다. 설정하지 않았다면 비워 두세요. 연결과 재연결은 상태를 읽기만 하고 음량이나 입력을 바꾸지 않습니다. IP 주소는 KEF Connect의 스피커 정보나 공유기 관리 화면에서 확인할 수 있습니다.", "This is the speaker's web page password, not your KEF account password. Leave it empty if you haven't set one. Connecting and reconnecting only read the status and never change the volume or input. You can find the IP address in the speaker info in KEF Connect or on your router's admin page.", "KEFアカウントのパスワードではなく、スピーカーのWebページのパスワードです。設定していない場合は空欄のままにしてください。接続と再接続は状態を読み取るだけで、音量や入力は変更しません。IPアドレスはKEF Connectのスピーカー情報やルーターの管理画面で確認できます。"))
            }

            if let device = controller.device {
                Section {
                    LabeledContent(loc("이름", "Name", "名前"), value: device.name)
                    LabeledContent(loc("모델", "Model", "モデル"), value: device.model)
                    if !KefModels.names.contains(device.model) {
                        ModelPicker()
                    }
                    FinishPicker(model: model.shownModel)
                    LabeledContent(loc("펌웨어", "Firmware", "ファームウェア"), value: device.firmware)
                    LabeledContent(loc("검증 상태", "Verification", "検証状況"), value: device.verifiedModel ? loc("실기 검증됨", "Tested on a real speaker", "実機で検証済み") : loc("미검증 모델 (공개 코드 기준)", "Unverified model (based on public code)", "未検証のモデル（公開コードに基づく）"))
                } header: {
                    Text(loc("기기 정보", "Device Info", "機器情報"))
                } footer: {
                    if KefModels.names.contains(device.model) {
                        Text(wrapping: loc("색상은 패널의 스피커 그림에 쓰입니다. 스피커가 색상을 알려 주지 않아 직접 고릅니다.", "The finish is used for the speaker in the panel. Speakers don't report their color, so pick it here.", "カラーはパネルのスピーカーの絵に使われます。スピーカーは色を知らせないため、ここで選びます。"))
                    } else {
                        Text(wrapping: loc("앱이 아직 모르는 모델입니다. 고른 모델은 패널의 스피커 그림과 색상 목록에만 쓰고, 입력 목록은 바꾸지 않습니다. 색상은 스피커가 알려 주지 않아 직접 고릅니다.", "The app doesn't know this model yet. The model you pick is used only for the speaker in the panel and the finish list; the inputs stay as they are. Speakers don't report their color, so pick it here.", "アプリがまだ知らないモデルです。選んだモデルはパネルのスピーカーの絵とカラーの一覧にだけ使い、入力の一覧は変えません。スピーカーは色を知らせないため、カラーはここで選びます。"))
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 500, height: controller.device.map { KefModels.names.contains($0.model) ? CGFloat(540) : 600 } ?? 300)
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
        case .online: loc("연결됨", "Connected", "接続済み")
        case .connecting: loc("연결 중…", "Connecting…", "接続中…")
        case let .offline(message): message
        case .idle: loc("설정되지 않음", "Not set up", "未設定")
        }
    }
}

/// For a speaker that reports a model the app does not know: the model to draw it as and list finishes for.
/// The inputs stay those of the reported model, so a wrong pick never hides an input the speaker has.
private struct ModelPicker: View {
    @Environment(AppModel.self) private var appModel

    var body: some View {
        Picker(loc("모델 선택", "Choose Model", "モデルを選択"), selection: Binding(get: { appModel.pickedModel }, set: { appModel.pickedModel = $0 })) {
            Label {
                Text(loc("고르지 않음", "None", "選択しない"))
            } icon: {
                Image(nsImage: SpeakerArt.image(nil, size: 16))
            }
            .tag(String?.none)
            Divider()
            ForEach(KefModels.names, id: \.self) { name in
                Label {
                    Text(name)
                } icon: {
                    Image(nsImage: SpeakerArt.image(nil, model: name, size: 16))
                }
                .tag(Optional(name))
            }
        }
    }
}

/// The finish the panel draws the speaker in. Lists the shown model's finishes;
/// only the app's own colors before the model is known.
private struct FinishPicker: View {
    @Environment(AppModel.self) private var appModel
    let model: String?

    var body: some View {
        Picker(loc("색상", "Finish", "カラー"), selection: Binding(get: { appModel.speakerFinish?.id }, set: { appModel.speakerFinishID = $0 })) {
            Label {
                Text(loc("앱 기본", "App default", "アプリの既定"))
            } icon: {
                Image(nsImage: SpeakerArt.image(nil, model: model, size: 16))
            }
            .tag(String?.none)
            ForEach(KefFinish.finishes(for: model)) { finish in
                Label {
                    Text(finish.name)
                } icon: {
                    Image(nsImage: SpeakerArt.image(finish, size: 16))
                }
                .tag(Optional(finish.id))
            }
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
                        ), in: 1...100)
                        .labelsHidden()
                        .frame(width: 150)
                        TextField(loc("앱 상한", "App limit", "アプリの上限"), value: $model.appLimit, format: .number)
                            .labelsHidden()
                            .multilineTextAlignment(.trailing)
                            .frame(width: 44)
                    }
                } label: {
                    Text(loc("앱 상한", "App limit", "アプリの上限"))
                    Text(loc("이 앱에서 올릴 수 있는 최대 음량", "The highest volume this app raises to", "このアプリで上げられる最大の音量"))
                }
                LabeledContent {
                    Text(deviceLimitText(controller))
                } label: {
                    Text(loc("KEF 기기 최대 음량", "KEF device maximum", "KEF機器の最大音量"))
                    Text(loc("KEF Connect에서 정한 값. 둘 중 낮은 값을 씁니다", "Set in KEF Connect. The lower of the two applies", "KEF Connectで設定した値。低いほうを使います"))
                }
                LabeledContent(loc("지금 적용 중인 상한", "Limit in effect", "現在の上限")) {
                    Text(verbatim: "\(controller.effectiveLimit) (\(LimitText.source(controller)))")
                        .monospacedDigit()
                }
            } header: {
                Text(loc("상한", "Limit", "上限"))
            } footer: {
                Text(wrapping: loc("낮추기는 언제나 됩니다. 리모컨과 KEF Connect에서 바꾸는 음량은 제한하지 않습니다.", "Lowering always works. Volume changed with the remote or KEF Connect isn't limited.", "下げる操作はいつでもできます。リモコンやKEF Connectで変えた音量は制限しません。"))
            }

            Section(loc("음량 보정", "Volume Correction", "音量補正")) {
                // Laid out by hand so the (i) button can sit right after the title.
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 4) {
                            Text(loc("정확한 음량 맞추기", "Accurate volume", "正確な音量合わせ"))
                            ApproachInfoButton()
                        }
                        Text(loc("올릴 때 목표보다 살짝 높게 보낸 뒤 목표로 내립니다", "When raising, sends a slightly higher value first and then settles on the target", "上げるときは目標より少し高く送ってから目標に下げます"))
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                    Spacer()
                    Toggle(loc("정확한 음량 맞추기", "Accurate volume", "正確な音量合わせ"), isOn: $model.approachFromAbove)
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
        guard let device = controller.device else { return loc("연결 후 표시", "Shown once connected", "接続後に表示") }
        return device.deviceVolumeLimit.map(String.init) ?? loc("꺼짐", "Off", "オフ")
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
        .help(loc("KEF 스피커는 같은 숫자라도 아래에서 올라올 때 더 작게 들리는 구간이 있습니다.", "In some ranges a KEF speaker sounds quieter at the same number when the volume comes up from below.", "KEFスピーカーには、同じ数値でも下から上げたときに小さく聞こえる範囲があります。"))
        .accessibilityLabel(loc("정확한 음량 맞추기 설명", "About accurate volume", "正確な音量合わせの説明"))
        .popover(isPresented: $isPresented, arrowEdge: .bottom) {
            VStack(alignment: .leading, spacing: 8) {
                Text(loc("정확한 음량 맞추기", "Accurate volume", "正確な音量合わせ")).font(.headline)
                Text(wrapping: loc("KEF 스피커는 같은 숫자라도 아래에서 올라올 때 더 작게 들리는 구간이 있습니다. 켜면 음량을 올릴 때 목표보다 조금 높은 값을 먼저 보내고 0.1초 뒤 목표 값을 보내, 위에서 내려와 맞춘 것과 같은 크기로 들리게 합니다.", "In some ranges a KEF speaker sounds quieter at the same number when the volume comes up from below. When this is on, raising the volume first sends a slightly higher value and then the target 0.1 seconds later, so it sounds as loud as coming down to the target from above.", "KEFスピーカーには、同じ数値でも下から上げたときに小さく聞こえる範囲があります。オンにすると、音量を上げるときに目標より少し高い値を先に送り、0.1秒後に目標の値を送ることで、上から下げて合わせたときと同じ大きさに聞こえるようにします。"))
                Text(wrapping: loc("예: 20으로 올리면 22를 거쳐 20, 12로 올리면 17을 거쳐 12가 됩니다. 5 이하와 41 이상은 바로 보냅니다. 잠깐 거치는 값도 상한을 넘지 않고, 내릴 때는 그대로 보냅니다.", "For example, raising to 20 goes through 22, and raising to 12 goes through 17. Values of 5 or less and 41 or more are sent directly. The value in between never goes past the limit, and lowering is sent as is.", "例: 20に上げると22を経て20に、12に上げると17を経て12になります。5以下と41以上はそのまま送ります。途中で経由する値も上限を超えず、下げるときはそのまま送ります。"))
                    .foregroundStyle(.secondary)
                Text(wrapping: loc("LSX II(USB 입력)에서 잰 표를 쓰며 LSX II LT에서는 아직 확인하지 않아 기본으로 꺼 둡니다.", "The table was measured on an LSX II (USB input) and hasn't been checked on the LSX II LT yet, so this is off by default.", "LSX II（USB入力）で測った表を使っており、LSX II LTではまだ確認していないため、既定ではオフです。"))
                    .foregroundStyle(.secondary)
            }
            .font(.callout)
            .padding(14)
            .frame(width: 320)
            .fixedSize(horizontal: false, vertical: true)
        }
    }
}
