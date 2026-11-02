import KefRemoteCore
import KefW2Driver
import ServiceManagement
import SwiftUI

/// Settings window with toolbar tabs (design M5): General, Speaker, Shortcuts. The volume limit and correction
/// belong to each speaker and sit on its page.
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
            ShortcutSettings()
                .tabItem { Label(loc("단축키", "Shortcuts", "ショートカット"), systemImage: "keyboard") }
                .tag(SettingsTab.shortcuts)
        }
        .onDisappear { model.stopRecording() }
    }
}

/// Global shortcuts with a recorder for each, the step per press and the indicator.
struct ShortcutSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                ForEach(ShortcutAction.allCases, id: \.self) { action in
                    ShortcutRow(action: action)
                }
                HStack {
                    Spacer()
                    Button(loc("기본값으로 되돌리기", "Restore Defaults", "デフォルトに戻す")) { model.resetShortcuts() }
                        .disabled(model.shortcuts == Shortcut.defaults)
                }
            } header: {
                Text(loc("단축키", "Shortcuts", "ショートカット"))
            } footer: {
                Text(wrapping: loc("칸을 누른 뒤 새 키 조합을 누르세요. esc는 취소, delete는 끄기입니다. 앱이 켜져 있는 동안 어느 앱에서든 동작하며, 손쉬운 사용 권한은 필요 없습니다.", "Click a box, then press the new key combination. esc cancels and delete turns it off. The shortcuts work in any app while this app runs, without Accessibility access.", "ボックスをクリックしてから新しいキーの組み合わせを押してください。escでキャンセル、deleteでオフになります。このアプリの起動中はどのアプリでも動作し、アクセシビリティの許可は必要ありません。"))
            }
            Section {
                Picker(selection: $model.volumeKeyStep) {
                    ForEach(VolumeSteps.choices, id: \.self) { step in
                        Text(step == VolumeSteps.perceptual ? loc("체감 음량 기준", "Perceived loudness", "体感音量") : "\(step)").tag(step)
                    }
                } label: {
                    Text(loc("한 번 누를 때 음량", "Volume per press", "1回押したときの音量"))
                    Text(loc("체감 음량 기준은 12, 15, 19, 22 …처럼 소리 크기가 고르게 늘도록 움직입니다", "Perceived loudness follows steps such as 12, 15, 19, 22 … so the loudness rises evenly", "体感音量は12、15、19、22 …のように音の大きさが均等に増えるように動きます"))
                }
                Toggle(isOn: $model.showsVolumeHUD) {
                    Text(loc("음량을 바꿀 때 화면에 표시", "Show the volume on screen", "音量を変えたときに画面に表示"))
                    Text(loc("메뉴 막대 아래 오른쪽에 잠깐 보여 줍니다. 꺼 두어도 연결 문제와 상한은 표시합니다", "Shows briefly at the top right, under the menu bar. Connection problems and the limit show even when this is off", "メニューバーの下の右側に少しの間表示します。オフでも接続の問題と上限は表示します"))
                }
            } header: {
                Text(loc("동작", "Behavior", "動作"))
            }
        }
        .formStyle(.grouped)
        .frame(width: 500, height: 540)
        .onDisappear { model.stopRecording() }
    }
}

/// The action, its shortcut as a button that records the next key press, and a
/// line when it couldn't be registered.
private struct ShortcutRow: View {
    @Environment(AppModel.self) private var model
    let action: ShortcutAction

    var body: some View {
        let recording = model.recordingShortcut == action
        VStack(alignment: .leading, spacing: 4) {
            LabeledContent {
                Button {
                    if recording { model.stopRecording() } else { model.startRecording(action) }
                } label: {
                    Text(label(recording: recording))
                        .monospacedDigit()
                        .foregroundStyle(recording || model.shortcuts[action] == nil ? Color.secondary : Color.primary)
                        .frame(minWidth: 110)
                }
                .buttonStyle(.bordered)
                .accessibilityLabel(loc("\(action.name) 단축키", "\(action.name) shortcut", "\(action.name)のショートカット"))
                .accessibilityValue(model.shortcuts[action]?.display ?? loc("끔", "Off", "オフ"))
            } label: {
                Label(action.name, systemImage: action.symbol)
            }
            if recording, let error = model.recordingError {
                Text(error).font(.callout).foregroundStyle(.red)
            } else if model.shortcutFailures.contains(action) {
                Text(wrapping: loc("다른 앱이 이미 쓰고 있어 등록하지 못했습니다. 다른 조합으로 바꿔 주세요.", "Another app already uses it, so it couldn't be registered. Choose another combination.", "ほかのアプリがすでに使っているため登録できませんでした。別の組み合わせに変更してください。"))
                    .font(.callout)
                    .foregroundStyle(.red)
            } else if action == .nextSpeaker {
                Text(wrapping: loc("스피커가 2대 이상일 때 선택한 스피커를 목록 순서대로 바꿉니다.", "With two or more speakers, moves to the next one in the list.", "スピーカーが2台以上のとき、選択中のスピーカーを一覧の順に切り替えます。"))
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func label(recording: Bool) -> String {
        if recording { return loc("키 조합을 누르세요", "Press keys", "キーを押してください") }
        return model.shortcuts[action]?.display ?? loc("끔", "Off", "オフ")
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
            Section {
                Toggle(isOn: $model.showsNowPlaying) {
                    Text(loc("재생 중인 곡 표시", "Show what's playing", "再生中の曲を表示"))
                    Text(loc("Wi-Fi나 블루투스로 재생할 때 음량 위에 곡 정보와 재생 버튼을 보여 줍니다", "When the speaker plays over Wi-Fi or Bluetooth, shows the track and playback buttons above the volume", "Wi-FiやBluetoothで再生しているとき、音量の上に曲の情報と再生ボタンを表示します"))
                }
            } header: {
                Text(loc("패널", "Panel", "パネル"))
            }
            UpdateSettings()
        }
        .formStyle(.grouped)
        .frame(width: 500, height: 520)
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

/// Speakers. One speaker shows its page right here; several are a list like the device lists of System Settings,
/// each with an (i) that opens its page. Everything on a page belongs to that speaker: address, password, model,
/// finish, limit and volume correction.
struct SpeakerSettings: View {
    @Environment(AppModel.self) private var model

    @State private var shown: SpeakerSlot?
    @State private var adding = false

    var body: some View {
        Group {
            if model.speakers.isEmpty {
                Form {
                    AddSpeakerSection(first: true) { _ in }
                }
                .formStyle(.grouped)
                .frame(width: 500, height: 330)
            } else if model.speakers.count == 1, let slot = model.speakers.first {
                SpeakerPage(slot: slot) {
                    Section {
                        HStack {
                            Spacer()
                            Button(loc("스피커 추가…", "Add Speaker…", "スピーカーを追加…")) { adding = true }
                        }
                    } footer: {
                        Text(wrapping: loc("다른 방의 스피커도 추가하면 메뉴 막대 패널에서 골라 조작할 수 있습니다.", "Add speakers in other rooms to pick the one to control in the menu bar panel.", "ほかの部屋のスピーカーも追加すると、メニューバーのパネルで選んで操作できます。"))
                    }
                }
                .frame(width: 500, height: 640)
            } else {
                SpeakerListForm(shown: $shown, adding: $adding)
                    .frame(width: 500, height: 420)
            }
        }
        .sheet(item: $shown) { slot in
            SpeakerSheet(slot: slot) { shown = nil }
        }
        .sheet(isPresented: $adding) {
            AddSpeakerSheet { adding = false }
        }
    }
}

/// Two or more speakers: one row each, with the model and the (i) for its page.
private struct SpeakerListForm: View {
    @Environment(AppModel.self) private var model
    @Binding var shown: SpeakerSlot?
    @Binding var adding: Bool

    var body: some View {
        Form {
            Section {
                ForEach(model.speakers) { slot in
                    HStack(spacing: 10) {
                        Image(nsImage: SpeakerArt.image(slot.finish, model: slot.shownModel, size: 24))
                            .frame(width: 30, height: 30)
                            .background(Circle().fill(Color.secondary.opacity(0.12)))
                            .accessibilityHidden(true)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(slot.name).lineLimit(1)
                            Text(slot.controller.device?.model ?? slot.entry.model ?? slot.entry.host)
                                .font(.subheadline)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                        Spacer(minLength: 8)
                        if slot.id == model.selected?.id {
                            Text(loc("선택됨", "Selected", "選択中")).foregroundStyle(.secondary)
                        } else if slot.unreachable {
                            Text(SpeakerSlot.notConnected).foregroundStyle(.secondary)
                        }
                        Button {
                            shown = slot
                        } label: {
                            Image(systemName: "info.circle")
                        }
                        .buttonStyle(.borderless)
                        .help(loc("\(slot.name) 설정", "\(slot.name) settings", "\(slot.name)の設定"))
                        .accessibilityLabel(loc("\(slot.name) 설정", "\(slot.name) settings", "\(slot.name)の設定"))
                    }
                }
            } footer: {
                Text(wrapping: loc("메뉴 막대와 단축키는 선택한 스피커를 조작합니다. 선택은 메뉴 막대 패널에서 바꿉니다.", "The menu bar and shortcuts control the selected speaker. Change it in the menu bar panel.", "メニューバーとショートカットは選択中のスピーカーを操作します。選択はメニューバーのパネルで変えます。"))
            }
            Section {
                HStack {
                    Spacer()
                    Button(loc("스피커 추가…", "Add Speaker…", "スピーカーを追加…")) { adding = true }
                }
            }
        }
        .formStyle(.grouped)
        .task { await model.refreshOthers() }
    }
}

/// A speaker's page over the list, closed with Done.
struct SpeakerSheet: View {
    let slot: SpeakerSlot
    let done: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            SpeakerPage(slot: slot, removed: done) { EmptyView() }
            Divider()
            HStack {
                Spacer()
                Button(loc("완료", "Done", "完了"), action: done)
                    .keyboardShortcut(.cancelAction)
            }
            .padding(12)
        }
        .frame(width: 500, height: 620)
    }
}

private struct AddSpeakerSheet: View {
    let done: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            Form {
                AddSpeakerSection(first: false) { _ in done() }
            }
            .formStyle(.grouped)
            Divider()
            HStack {
                Spacer()
                Button(loc("취소", "Cancel", "キャンセル"), action: done)
                    .keyboardShortcut(.cancelAction)
            }
            .padding(12)
        }
        .frame(width: 460, height: 340)
    }
}

/// Address and password of a speaker to add. It is added only with an address the app accepts and that no other
/// speaker uses; connecting afterwards only reads.
private struct AddSpeakerSection: View {
    @Environment(AppModel.self) private var model
    let first: Bool
    let added: (SpeakerSlot) -> Void

    @State private var host = ""
    @State private var password = ""
    @State private var error: String?

    var body: some View {
        Section {
            TextField(loc("IP 주소", "IP address", "IPアドレス"), text: $host, prompt: Text("192.168.0.14"))
            SecureField(loc("웹페이지 암호", "Web page password", "Webページのパスワード"), text: $password, prompt: Text(loc("없음", "None", "なし")))
            HStack(spacing: 8) {
                if let error {
                    StatusDot(color: .red)
                    Text(error).foregroundStyle(.red).lineLimit(2)
                }
                Spacer()
                Button(first ? loc("저장하고 연결", "Save and Connect", "保存して接続") : loc("추가", "Add", "追加")) {
                    switch model.addSpeaker(host: host, password: password) {
                    case let .success(slot):
                        host = ""
                        password = ""
                        error = nil
                        added(slot)
                    case let .failure(.message(message)):
                        error = message
                    }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        } header: {
            Text(first ? loc("연결", "Connection", "接続") : loc("스피커 추가", "Add Speaker", "スピーカーを追加"))
        } footer: {
            ConnectionFooter()
        }
    }
}

private struct ConnectionFooter: View {
    var body: some View {
        Text(wrapping: loc("KEF 계정 암호가 아니라 스피커 웹페이지 암호입니다. 설정하지 않았다면 비워 두세요. 연결과 재연결은 상태를 읽기만 하고 음량이나 입력을 바꾸지 않습니다. IP 주소는 KEF Connect의 스피커 정보나 공유기 관리 화면에서 확인할 수 있습니다.", "This is the speaker's web page password, not your KEF account password. Leave it empty if you haven't set one. Connecting and reconnecting only read the status and never change the volume or input. You can find the IP address in the speaker info in KEF Connect or on your router's admin page.", "KEFアカウントのパスワードではなく、スピーカーのWebページのパスワードです。設定していない場合は空欄のままにしてください。接続と再接続は状態を読み取るだけで、音量や入力は変更しません。IPアドレスはKEF Connectのスピーカー情報やルーターの管理画面で確認できます。"))
    }
}

/// One speaker's settings: connection, what it is, its volume limit and correction, and removing it.
private struct SpeakerPage<Extra: View>: View {
    @Environment(AppModel.self) private var model
    let slot: SpeakerSlot
    var removed: () -> Void = {}
    @ViewBuilder let extra: Extra

    @State private var confirmingRemoval = false

    var body: some View {
        let controller = slot.controller
        Form {
            ConnectionSection(slot: slot)

            if let device = controller.device {
                Section {
                    LabeledContent(loc("이름", "Name", "名前"), value: device.name)
                    LabeledContent(loc("모델", "Model", "モデル"), value: device.model)
                    if !KefModels.names.contains(device.model) {
                        ModelPicker(slot: slot)
                    }
                    FinishPicker(slot: slot)
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

            VolumeSections(slot: slot)

            Section {
                HStack {
                    Spacer()
                    Button(loc("스피커 삭제…", "Remove Speaker…", "スピーカーを削除…"), role: .destructive) { confirmingRemoval = true }
                        .confirmationDialog(
                            loc("이 스피커를 목록에서 지울까요?", "Remove \(slot.name) from the list?", "\(slot.name)を一覧から削除しますか？"),
                            isPresented: $confirmingRemoval
                        ) {
                            Button(loc("삭제", "Remove", "削除"), role: .destructive) {
                                model.remove(slot)
                                removed()
                            }
                        } message: {
                            Text(loc("\(slot.name)의 주소, 암호, 상한 같은 설정도 함께 지웁니다. 스피커 자체는 바뀌지 않습니다.", "Its address, password, limit and other settings go with it. Nothing changes on the speaker itself.", "アドレス、パスワード、上限などこのスピーカーの設定も削除します。スピーカー自体は変わりません。"))
                        }
                }
            }

            extra
        }
        .formStyle(.grouped)
        .task(id: slot.id) {
            await model.refresh(slot)
            await model.refreshDeviceIfStale(slot)
        }
    }
}

private struct ConnectionSection: View {
    @Environment(AppModel.self) private var model
    let slot: SpeakerSlot

    @State private var hostDraft = ""
    @State private var passwordDraft = ""
    @State private var connectionError: String?
    @State private var hasPassword = false

    var body: some View {
        let phase = slot.controller.phase
        Section {
            TextField(loc("IP 주소", "IP address", "IPアドレス"), text: $hostDraft, prompt: Text("192.168.0.14"))
            SecureField(loc("웹페이지 암호", "Web page password", "Webページのパスワード"), text: $passwordDraft, prompt: Text(hasPassword ? loc("저장됨 (바꿀 때만 입력)", "Saved (enter only to change it)", "保存済み（変更する場合のみ入力）") : loc("없음", "None", "なし")))
            HStack(spacing: 8) {
                if let connectionError {
                    StatusDot(color: .red)
                    Text(connectionError).foregroundStyle(.red)
                } else {
                    StatusDot(color: statusColor(phase))
                    Text(statusText(phase))
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                }
                Spacer()
                if hasPassword {
                    Button(loc("암호 삭제", "Delete Password", "パスワードを削除")) {
                        connectionError = model.saveConnection(slot, host: hostDraft, password: "")
                        hasPassword = model.hasPassword(slot)
                    }
                }
                Button(loc("저장하고 연결", "Save and Connect", "保存して接続")) {
                    connectionError = model.saveConnection(slot, host: hostDraft, password: passwordDraft.isEmpty ? nil : passwordDraft)
                    passwordDraft = ""
                    hasPassword = model.hasPassword(slot)
                }
                .keyboardShortcut(.defaultAction)
            }
        } header: {
            Text(loc("연결", "Connection", "接続"))
        } footer: {
            ConnectionFooter()
        }
        .task(id: slot.id) {
            hostDraft = slot.entry.host
            passwordDraft = ""
            connectionError = nil
            hasPassword = model.hasPassword(slot)
        }
    }

    private func statusColor(_ phase: SpeakerController.Phase) -> Color {
        switch phase {
        case .online: .green
        case .connecting: .yellow
        case .offline: .red
        case .idle: slot.unreachable ? .red : .secondary
        }
    }

    /// The full error stays here; the panel only says what to do (design M4).
    private func statusText(_ phase: SpeakerController.Phase) -> String {
        switch phase {
        case .online: loc("연결됨", "Connected", "接続済み")
        case .connecting: loc("연결 중…", "Connecting…", "接続中…")
        case let .offline(message): message
        case .idle: slot.entry.isConfigured ? SpeakerSlot.notConnected : loc("설정되지 않음", "Not set up", "未設定")
        }
    }
}

/// The app's limit for this speaker next to the KEF device maximum, and the volume correction.
private struct VolumeSections: View {
    @Environment(AppModel.self) private var model
    let slot: SpeakerSlot

    var body: some View {
        let controller = slot.controller
        let limit = Binding(
            get: { slot.entry.appLimit },
            set: { value in model.update(slot) { $0.appLimit = value } }
        )
        Section {
            LabeledContent {
                HStack(spacing: 8) {
                    Slider(value: Binding(
                        get: { Double(limit.wrappedValue) },
                        set: { limit.wrappedValue = Int($0.rounded()) }
                    ), in: 1...100)
                    .labelsHidden()
                    .frame(width: 150)
                    TextField(loc("앱 상한", "App limit", "アプリの上限"), value: limit, format: .number)
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
            Text(loc("음량 상한", "Volume Limit", "音量の上限"))
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
                Toggle(loc("정확한 음량 맞추기", "Accurate volume", "正確な音量合わせ"), isOn: Binding(
                    get: { slot.entry.approachFromAbove },
                    set: { value in model.update(slot) { $0.approachFromAbove = value } }
                ))
                .toggleStyle(.switch)
                .labelsHidden()
            }
        }
    }

    private func deviceLimitText(_ controller: SpeakerController) -> String {
        guard let device = controller.device else { return loc("연결 후 표시", "Shown once connected", "接続後に表示") }
        return device.deviceVolumeLimit.map(String.init) ?? loc("꺼짐", "Off", "オフ")
    }
}

/// For a speaker that reports a model the app does not know: the model to draw it as and list finishes for.
/// The inputs stay those of the reported model, so a wrong pick never hides an input the speaker has.
private struct ModelPicker: View {
    @Environment(AppModel.self) private var appModel
    let slot: SpeakerSlot

    var body: some View {
        Picker(loc("모델 선택", "Choose Model", "モデルを選択"), selection: Binding(
            get: { slot.entry.pickedModel },
            set: { value in appModel.update(slot) { $0.pickedModel = value } }
        )) {
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
    let slot: SpeakerSlot

    var body: some View {
        let model = slot.shownModel
        Picker(loc("색상", "Finish", "カラー"), selection: Binding(
            get: { slot.finish?.id },
            set: { value in appModel.update(slot) { $0.finish = value } }
        )) {
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
