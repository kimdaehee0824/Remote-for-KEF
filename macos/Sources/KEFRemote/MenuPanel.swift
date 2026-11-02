import AppKit
import KefRemoteCore
import KefW2Driver
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
                Label {
                    Text(wrapping: notice)
                } icon: {
                    Image(systemName: "info.circle")
                }
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if let version = AppUpdater.shared.waitingVersion {
                UpdateReminder(version: version)
            }
            PanelFooter { showSettings(nil) }
        }
        .padding(16)
        .frame(width: 320)
        // Beside the panel, its arrow on the middle of the panel's side. Pointed at the name, the arrow ended up in
        // the list's top corner: the panel hangs from the menu bar, so the list can't rise to centre on the name.
        // NSPopover moves it to the other side when the panel is too close to the left of the screen.
        .popover(isPresented: Bindable(model).speakerListOpen, attachmentAnchor: .rect(.bounds), arrowEdge: .leading) {
            SpeakerListPopover { showSettings(.speaker) }
                .environment(model)
        }
        .onAppear { model.panelDidAppear() }
        .onDisappear { model.panelDidDisappear() }
        // The other speakers are read while the list shows them, and again every few seconds.
        .task(id: model.speakerListOpen) {
            guard model.speakerListOpen else { return }
            while !Task.isCancelled {
                await model.refreshOthers()
                try? await Task.sleep(for: AppModel.listRefresh)
            }
        }
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
                title: loc("스피커를 연결하세요", "Connect a speaker", "スピーカーを接続してください"),
                text: loc("설정에서 스피커의 IP 주소를 입력하면 여기서 바로 음량과 입력을 바꿀 수 있습니다.", "Enter the speaker's IP address in Settings to change the volume and input right here.", "設定でスピーカーのIPアドレスを入力すると、ここで音量と入力をすぐに変えられます。")
            ) {
                Button(loc("스피커 설정…", "Speaker Settings…", "スピーカーの設定…")) { showSettings(.speaker) }
            }
        case .offline:
            PanelMessage(
                title: loc("스피커가 응답하지 않습니다", "The speaker isn't responding", "スピーカーが応答しません"),
                text: loc("스피커 전원과 Wi-Fi 연결을 확인하세요. 같은 네트워크인데도 안 되면 시스템 설정에서 로컬 네트워크 권한을 확인하세요. 잠시 뒤 자동으로 다시 시도합니다.", "Check the speaker's power and Wi-Fi connection. If it's on the same network and still can't be reached, check Local Network access in System Settings. The app retries in a moment.", "スピーカーの電源とWi-Fi接続を確認してください。同じネットワークなのに接続できない場合は、システム設定でローカルネットワークへのアクセスを確認してください。しばらくすると自動で再試行します。")
            ) {
                Button(loc("다시 연결", "Reconnect", "再接続")) { model.connect() }
                Button(loc("스피커 설정…", "Speaker Settings…", "スピーカーの設定…")) { showSettings(.speaker) }
            }
        case .connecting, .online:
            if let state = controller.snapshot, let device = controller.device {
                if state.power == .standby {
                    StandbySection(device: device)
                } else {
                    if controller.showsPlayback {
                        NowPlayingModule(playback: controller.playback)
                    }
                    VolumeSection(state: state)
                    if device.capabilities.contains(.source), device.sources.count > 1 {
                        SourceSection(device: device, state: state)
                    }
                }
            } else {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(loc("스피커 상태를 읽는 중", "Reading the speaker's status", "スピーカーの状態を読み込み中")).foregroundStyle(.secondary)
                }
            }
        }
    }

    private func showSettings(_ tab: SettingsTab?) {
        model.openSettings(tab, using: openSettings)
    }
}

/// An update the daily check found while the panel was closed (Sparkle's gentle reminders).
/// Install opens Sparkle's window with the release notes.
private struct UpdateReminder: View {
    let version: String

    var body: some View {
        HStack(spacing: 8) {
            Label {
                Text(loc("\(version) 버전을 설치할 수 있습니다", "Version \(version) is available", "バージョン \(version) をインストールできます"))
            } icon: {
                Image(systemName: "arrow.down.circle")
            }
                .font(.callout)
                .foregroundStyle(.secondary)
            Spacer(minLength: 8)
            Button(loc("설치…", "Install…", "インストール…")) { AppUpdater.shared.checkForUpdates() }
                .controlSize(.small)
        }
    }
}

// MARK: Header

/// Name and model, with the power switch where Control Center puts the Wi-Fi
/// and Bluetooth switches (design M1, 2).
private struct PanelHeader: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let controller = model.controller
        let isOn = controller.isOnline && controller.snapshot?.power == .on
        HStack(spacing: 10) {
            // The speaker's model in the finish picked in settings, or the app's Royal Blue, dimmed while it is not on.
            Image(nsImage: SpeakerArt.image(model.speakerFinish, model: model.shownModel, size: 24))
                .frame(width: 30, height: 30)
                .background(Circle().fill(Color.secondary.opacity(0.12)))
                .opacity(isOn ? 1 : 0.6)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                SpeakerName()
                Text(subtitle(controller))
                    .font(.subheadline)
                    .foregroundStyle(subtitleIsError(controller) ? Color.red : Color.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 8)
            Toggle(loc("전원", "Power", "電源"), isOn: powerBinding(controller))
                .toggleStyle(.switch)
                .labelsHidden()
                .disabled(!canTogglePower(controller))
                .help(controller.snapshot?.power == .on ? loc("대기 상태로 끄기", "Turn off to standby", "スタンバイにする") : loc("마지막 입력으로 켜기", "Turn on with the last input", "最後の入力で電源を入れる"))
        }
    }

    private func subtitle(_ c: SpeakerController) -> String {
        switch c.activity {
        case let .source(id)?:
            let name = SourceNames.short(c.device?.sources.first { $0.id == id }?.name ?? id)
            return loc("\(name)\(Korean.ro(name)) 바꾸는 중", "Switching to \(name)", "\(name)に切り替え中")
        case .powerOn?:
            return loc("켜는 중", "Turning on", "電源を入れています")
        case .powerOff?:
            return loc("끄는 중", "Turning off", "電源を切っています")
        default:
            break
        }
        switch c.phase {
        case .idle:
            return loc("스피커를 설정하세요", "Set up your speaker", "スピーカーを設定してください")
        case .connecting:
            return c.snapshot == nil ? loc("연결하는 중", "Connecting", "接続中") : (c.device?.model ?? "")
        case .offline:
            return loc("연결 끊김", "Offline", "オフライン")
        case .online:
            guard let device = c.device else { return "" }
            if c.isWaking { return loc("켜는 중", "Turning on", "電源を入れています") }
            if c.snapshot?.power == .standby { return loc("대기", "Standby", "スタンバイ") }
            // Whether the model was tested on a real speaker is in settings only (Device Info).
            return device.model
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
                default: (c.isOnline && c.snapshot?.power == .on) || c.isWaking
                }
            },
            set: { on in
                if on { c.wake() } else { c.standby() }
            }
        )
    }

    private func canTogglePower(_ c: SpeakerController) -> Bool {
        c.isOnline && !c.isBusy && !c.isWaking && c.snapshot != nil && c.device?.capabilities.contains(.power) == true
    }
}

/// The selected speaker's name. With more than one speaker it is a button with a chevron that opens the list
/// beside the panel. Nothing is filled behind it in any state: the chevron turning from > to < says the list is open.
private struct SpeakerName: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let name = model.selected?.name ?? "Remote for KEF"
        if model.hasSeveral {
            Button {
                model.toggleSpeakerList()
            } label: {
                HStack(spacing: 4) {
                    Text(name)
                        .font(.headline)
                        .lineLimit(1)
                    Image(systemName: model.speakerListOpen ? "chevron.left" : "chevron.right")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(.secondary)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(loc("스피커 바꾸기", "Switch speakers", "スピーカーを切り替える"))
            .accessibilityLabel(name)
            .accessibilityHint(loc("스피커 목록을 엽니다", "Opens the speaker list", "スピーカーの一覧を開きます"))
        } else {
            Text(name)
                .font(.headline)
                .lineLimit(1)
        }
    }
}

// MARK: Speaker list

/// Every registered speaker with its state and power switch, like the device lists of Control Center. A row
/// selects that speaker and closes the list; its switch only turns that speaker on or off.
struct SpeakerListPopover: View {
    @Environment(AppModel.self) private var model
    let showSettings: () -> Void

    @State private var turningAllOff = false

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(loc("스피커", "Speakers", "スピーカー"))
                .font(.headline)
                .padding(.horizontal, 8)
                .padding(.bottom, 4)
            ForEach(model.speakers) { slot in
                SpeakerListRow(slot: slot, selected: slot.id == model.selected?.id) {
                    model.select(slot)
                    model.speakerListOpen = false
                }
            }
            Divider()
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
            MenuRow(title: loc("모두 끄기", "Turn All Off", "すべてオフ"), disabled: turningAllOff) {
                turningAllOff = true
                Task {
                    await model.turnAllOff()
                    turningAllOff = false
                }
            }
            MenuRow(title: loc("스피커 설정…", "Speaker Settings…", "スピーカーの設定…"), disabled: false) {
                model.speakerListOpen = false
                showSettings()
            }
        }
        .padding(.top, 10)
        .padding([.horizontal, .bottom], 6)
        .frame(width: 260)
    }
}

private struct SpeakerListRow: View {
    @Environment(AppModel.self) private var model
    let slot: SpeakerSlot
    let selected: Bool
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        let c = slot.controller
        let reachable = c.isOnline
        HStack(spacing: 10) {
            Button(action: action) {
                HStack(spacing: 10) {
                    Image(nsImage: SpeakerArt.monochrome(model: slot.shownModel, size: 20))
                        .renderingMode(.template)
                        .foregroundStyle(selected ? Color.white : Color.primary)
                        .frame(width: 26, height: 26)
                        .background(Circle().fill(selected ? Color.accentColor : Color.secondary.opacity(0.18)))
                        .opacity(reachable || selected ? 1 : 0.5)
                    VStack(alignment: .leading, spacing: 0) {
                        Text(slot.name)
                            .lineLimit(1)
                        Text(slot.listStatus)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 4)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityAddTraits(selected ? .isSelected : [])
            Toggle(loc("\(slot.name) 전원", "\(slot.name) power", "\(slot.name)の電源"), isOn: power)
                .toggleStyle(.switch)
                .controlSize(.mini)
                .labelsHidden()
                .disabled(!canTogglePower)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(RoundedRectangle(cornerRadius: 8).fill(hovering ? Color.primary.opacity(0.08) : Color.clear))
        .onHover { hovering = $0 }
    }

    private var power: Binding<Bool> {
        let c = slot.controller
        return Binding(
            get: {
                switch c.activity {
                case .powerOn?: true
                case .powerOff?: false
                default: slot.isOn || c.isWaking
                }
            },
            set: { on in
                Task { await model.setPower(slot, on: on) }
            }
        )
    }

    private var canTogglePower: Bool {
        let c = slot.controller
        return c.isOnline && !c.isBusy && !c.isWaking && c.snapshot != nil && c.device?.capabilities.contains(.power) == true
    }
}

/// A text row at the foot of the list, like a menu item.
private struct MenuRow: View {
    let title: String
    let disabled: Bool
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Text(title)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .contentShape(Rectangle())
                .background(RoundedRectangle(cornerRadius: 8).fill(hovering && !disabled ? Color.primary.opacity(0.08) : Color.clear))
        }
        .buttonStyle(.plain)
        .disabled(disabled)
        .onHover { hovering = $0 }
    }
}

// MARK: Now playing

/// What the speaker plays, as its own glass tile above the volume, with what the
/// Now Playing module in Control Center shows (measured on macOS 27): a 40 pt cover,
/// the title, "artist – album" and previous, play/pause and next in the secondary
/// color. Laid out in one row, since the panel is wider than the module.
///
/// The cover keeps only a slight rounding, as album art usually has, and the tile's
/// corners are concentric with it: tile radius = cover radius + the inset between them.
private struct NowPlayingModule: View {
    static let inset: CGFloat = 10
    static var tileRadius: CGFloat { Artwork.radius + inset }

    @Environment(AppModel.self) private var model
    let playback: PlaybackInfo

    var body: some View {
        let controller = model.controller
        let playing = playback.status == .playing
        let tile = HStack(spacing: 10) {
            Artwork(url: playback.artworkURL)
            VStack(alignment: .leading, spacing: 1) {
                Text(playback.title)
                    .font(.body.weight(.semibold))
                    .lineLimit(1)
                if let subtitle {
                    Text(subtitle)
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            .help(details)
            Spacer(minLength: 4)
            HStack(spacing: 4) {
                TransportButton(symbol: "backward.fill", size: 14, name: loc("이전 곡", "Previous", "前の曲"), enabled: playback.canPrevious) {
                    controller.sendPlayback(.previous)
                }
                TransportButton(
                    symbol: playing ? "pause.fill" : "play.fill", size: 19,
                    name: playing ? loc("일시 정지", "Pause", "一時停止") : loc("재생", "Play", "再生"),
                    enabled: playback.canTogglePause
                ) {
                    controller.sendPlayback(.togglePause)
                }
                TransportButton(symbol: "forward.fill", size: 14, name: loc("다음 곡", "Next", "次の曲"), enabled: playback.canNext) {
                    controller.sendPlayback(.next)
                }
            }
        }
        .padding(Self.inset)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(loc("재생 중", "Now Playing", "再生中"))

        let shape = RoundedRectangle(cornerRadius: Self.tileRadius, style: .continuous)
        #if compiler(>=6.2)
        if #available(macOS 26, *) {
            tile.glassEffect(.regular, in: shape)
        } else {
            tile.background(.quinary, in: shape)
        }
        #else
        tile.background(.quinary, in: shape)
        #endif
    }

    /// "Artist – Album" as in Control Center, or what there is of it, or the service.
    private var subtitle: String? {
        let parts = [playback.artist, playback.album].filter { !$0.isEmpty }
        return parts.isEmpty ? playback.service : parts.joined(separator: " – ")
    }

    /// Title, artist, album and service, for the tooltip.
    private var details: String {
        [playback.title, playback.artist, playback.album, playback.service ?? ""]
            .filter { !$0.isEmpty }
            .joined(separator: "\n")
    }
}

/// The cover the speaker reports, or a note while there is none or it can't be loaded.
private struct Artwork: View {
    /// Just enough to soften the corners.
    static let radius: CGFloat = 4

    let url: String?

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: Self.radius, style: .continuous)
        AsyncImage(url: url.flatMap(URL.init(string:))) { phase in
            if let image = phase.image {
                image.resizable().scaledToFill()
            } else {
                Image(systemName: "music.note")
                    .font(.system(size: 16, weight: .medium))
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.secondary.opacity(0.18))
            }
        }
        .frame(width: 40, height: 40)
        .clipShape(shape)
        .accessibilityHidden(true)
    }
}

/// A plain symbol in the secondary color like the buttons in the Control Center
/// module, with the hover highlight of the input rows.
private struct TransportButton: View {
    let symbol: String
    let size: CGFloat
    let name: String
    let enabled: Bool
    let action: () -> Void

    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: size, weight: .semibold))
                .foregroundStyle(.secondary)
                .frame(width: 30, height: 30)
                .contentShape(Circle())
                .background(Circle().fill(hovering && enabled ? Color.primary.opacity(0.08) : Color.clear))
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.4)
        .onHover { hovering = $0 }
        .help(name)
        .accessibilityLabel(name)
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
                Text(loc("음량", "Volume", "音量")).font(.headline)
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
                .help(state.muted ? loc("음소거 해제", "Unmute", "ミュート解除") : loc("음소거", "Mute", "ミュート"))
                .accessibilityLabel(state.muted ? loc("음소거 해제", "Unmute", "ミュート解除") : loc("음소거", "Mute", "ミュート"))

                // No `step`: on macOS it draws 101 tick marks. The binding rounds to whole numbers.
                Slider(value: binding(controller, limit: limit), in: 0...100) {
                    Text(loc("음량", "Volume", "音量"))
                } onEditingChanged: { editing in
                    isDragging = editing
                    if !editing { draft = nil }
                }
                .labelsHidden()
                .background(LimitMark(limit: limit))
                .accessibilityValue(loc("\(shown), 상한 \(limit)", "\(shown), limit \(limit)", "\(shown)、上限 \(limit)"))
            }
            HStack {
                if state.volume > limit {
                    Text(wrapping: loc("현재 음량이 상한(\(limit))보다 높습니다. 낮추기만 할 수 있습니다.", "The volume is above the limit (\(limit)). You can only lower it.", "現在の音量が上限（\(limit)）を超えています。下げる操作だけできます。"))
                        .foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Text(verbatim: LimitText.describe(limit, controller))
                        .foregroundStyle(.secondary)
                    Spacer()
                    if switching {
                        Text(loc("바꾸는 동안 잠시 음소거", "Muted briefly while switching", "切り替え中は一時的にミュート")).foregroundStyle(.secondary)
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

/// A tick at the limit with the range above it hatched, drawn behind the system
/// slider's track. Neutral, so the accent stays the only color in the panel.
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
                        .stroke(Color.secondary.opacity(0.5), lineWidth: 1)
                        .frame(width: max(0, proxy.size.width - inset - x), height: 10)
                        .clipShape(RoundedRectangle(cornerRadius: 2))
                        .offset(x: x, y: mid - 5)
                    RoundedRectangle(cornerRadius: 1)
                        .fill(Color.secondary)
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
        // A wake that goes on to switch inputs keeps showing the input it is heading for.
        let waitingOn = switchingTo ?? controller.activity.wakingSource(last: controller.lastActiveSource)
        VStack(alignment: .leading, spacing: 2) {
            Text(loc("입력", "Input", "入力")).font(.headline).padding(.bottom, 4)
            ForEach(device.sources) { source in
                SourceRow(
                    source: source,
                    selected: (waitingOn ?? state.sourceId) == source.id,
                    inProgress: waitingOn == source.id,
                    dimmed: waitingOn != nil && waitingOn != source.id,
                    disabled: controller.isBusy
                ) {
                    controller.selectSource(source.id)
                }
            }
            if switchingTo != nil {
                Text(wrapping: loc("전환이 끝나면 음량을 확인하고 음소거를 풉니다. 새 입력이 더 크면 음소거를 유지합니다.", "When the switch is done, the app checks the volume and unmutes. If the new input is louder, it stays muted.", "切り替えが終わると音量を確認してミュートを解除します。新しい入力のほうが大きい場合はミュートのままにします。"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 4)
            } else if waitingOn == nil, state.sourceId == nil {
                // Woken with the power switch before the app ever saw an input: the speaker doesn't say which.
                Text(wrapping: loc("스피커가 지금 입력을 알려 주지 않습니다. 입력을 고르면 표시됩니다.", "The speaker isn't saying which input it's on. It shows once you pick one.", "スピーカーが現在の入力を知らせていません。入力を選ぶと表示されます。"))
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
        // The input is back about 2 s before the power status and shows selected from then on. Before that, a wake
        // started here shows the input the speaker should come back on.
        let woken = controller.isWaking ? controller.snapshot?.sourceId : nil
        let wakingTo = controller.activity.wakingSource(last: woken ?? controller.lastActiveSource) ?? woken
        VStack(alignment: .leading, spacing: 2) {
            if device.capabilities.contains(.source), device.sources.count > 1 {
                Text(loc("이 입력으로 켜기", "Turn on with this input", "この入力で電源を入れる")).font(.headline).padding(.bottom, 4)
                ForEach(device.sources) { source in
                    SourceRow(
                        source: source,
                        selected: wakingTo == source.id,
                        detail: controller.lastActiveSource == source.id ? loc("마지막 입력", "Last input", "最後の入力") : nil,
                        inProgress: wakingTo == source.id,
                        dimmed: wakingTo != nil && wakingTo != source.id,
                        disabled: controller.isBusy || !canPower || controller.isWaking
                    ) {
                        controller.wake(to: source.id)
                    }
                }
            }
            Text(wrapping: canPower
                ? loc("켜기 전에 음량이 상한 이하인지 확인합니다. 대기 전 음량이 상한보다 높으면 켜지 않고 이유를 알려 드립니다.", "Before turning on, the app checks that the volume is within the limit. If the volume before standby is above it, the speaker stays off and the app says why.", "電源を入れる前に、音量が上限以下か確認します。スタンバイ前の音量が上限を超えている場合は電源を入れず、理由をお知らせします。")
                : loc("이 기기는 앱에서 켤 수 없습니다. 리모컨이나 KEF Connect로 켜 주세요.", "This device can't be turned on from the app. Turn it on with the remote or KEF Connect.", "この機器はアプリから電源を入れられません。リモコンかKEF Connectで入れてください。"))
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
                SourceGlyph(sourceId: source.id)
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
            Text(wrapping: text)
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
                Text(loc("설정", "Settings", "設定")).frame(maxWidth: .infinity)
            }
            .keyboardShortcut(",", modifiers: .command)
            .help(loc("설정", "Settings", "設定") + " (⌘,)")
            Button {
                NSApp.terminate(nil)
            } label: {
                Text(loc("종료", "Quit", "終了")).frame(maxWidth: .infinity)
            }
            .keyboardShortcut("q", modifiers: .command)
            .help(loc("Remote for KEF 종료", "Quit Remote for KEF", "Remote for KEFを終了") + " (⌘Q)")
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

/// The input's symbol. Bluetooth is AppKit's own Bluetooth image, which SF Symbols has no counterpart for.
struct SourceGlyph: View {
    let sourceId: String

    var body: some View {
        if sourceId == "bluetooth", let image = NSImage(named: NSImage.bluetoothTemplateName) {
            Image(nsImage: image)
                .renderingMode(.template)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(height: 13)
        } else {
            Image(systemName: SourceIcon.symbol(for: sourceId))
                .font(.system(size: 12, weight: .medium))
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

/// Where the limit in effect comes from, and "Limit 30 (app setting)" for the panel.
@MainActor
enum LimitText {
    static func source(_ controller: SpeakerController) -> String {
        controller.limitFromDevice
            ? loc("KEF 기기 설정", "KEF device setting", "KEF機器の設定")
            : loc("앱 설정", "app setting", "アプリの設定")
    }

    static func describe(_ limit: Int, _ controller: SpeakerController) -> String {
        loc("상한 \(limit)", "Limit \(limit)", "上限 \(limit)") + " (\(source(controller)))"
    }
}
