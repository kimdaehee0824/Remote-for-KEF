import AppKit
import Carbon.HIToolbox
import KefRemoteCore
import KefW2Driver
import SwiftUI

/// `--self-test <folder>`: runs the real panel and settings against a simulated
/// speaker, walks through every state, saves a PNG of each and a report, then
/// exits with 0 only if every step passed. The Mac counterpart of the Windows
/// self-test, for CI and for checking layout without a speaker.
///
/// Captures draw the view hierarchy offscreen (no screen recording permission).
/// System materials such as Liquid Glass are composited by the window server and
/// do not show in them; the controls and layout do.
@MainActor
enum SelfTest {
    static let folder: URL? = {
        let arguments = ProcessInfo.processInfo.arguments
        guard let index = arguments.firstIndex(of: "--self-test") else { return nil }
        let path = index + 1 < arguments.count ? arguments[index + 1] : "self-test"
        return URL(fileURLWithPath: path, isDirectory: true).standardizedFileURL
    }()

    struct Failure: Error, CustomStringConvertible {
        let description: String
    }

    static func run(in folder: URL) async -> Bool {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var report: [String] = []
        var failures = 0

        func step(_ name: String, _ body: () async throws -> Void) async {
            do {
                try await body()
                report.append("PASS \(name)")
            } catch {
                failures += 1
                report.append("FAIL \(name): \(error)")
            }
        }
        func expect(_ condition: Bool, _ what: @autoclosure () -> String) throws {
            if !condition { throw Failure(description: "expected " + what()) }
        }
        func waitFor(_ what: String, timeout: Duration = .seconds(8), _ condition: () -> Bool) async throws {
            let clock = ContinuousClock()
            let deadline = clock.now + timeout
            while !condition() {
                if clock.now > deadline { throw Failure(description: "timed out waiting for " + what) }
                try await Task.sleep(for: .milliseconds(50))
            }
        }

        let suite = "io.github.kimdaehee0824.KEFRemote.SelfTest"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        let model = AppModel(defaults: UserDefaults(suiteName: suite) ?? .standard, usesKeychain: false)
        let room = DemoSpeaker.room()
        let speaker = room[DemoSpeaker.deskAddress]!
        model.makeDriver = { entry, _ in DemoDriver(room[entry.host] ?? DemoSpeaker()) }
        // The selected speaker's, which changes once there are several.
        var controller: SpeakerController { model.controller }
        // Commands are queued on the next main actor turn, so wait for one to start and finish.
        func settle() async throws {
            try await Task.sleep(for: .milliseconds(80))
            try await waitFor("idle") { !controller.isBusy }
        }

        let panel = Capture(width: 320) { MenuPanel().environment(model) }
        let darkPanel = Capture(width: 320, appearance: .darkAqua) { MenuPanel().environment(model) }

        await step("panel, not set up") {
            try await panel.save(folder, "01-panel-setup.png")
        }
        await step("connect") {
            guard case .success = model.addSpeaker(host: DemoSpeaker.deskAddress, password: "") else { throw Failure(description: "added") }
            try expect(model.speakers.count == 1 && model.selected != nil && !model.hasSeveral, "one speaker, selected")
            try await waitFor("online") { controller.isOnline && controller.snapshot != nil }
            try expect(controller.listensForChanges, "the selected speaker listens for changes")
            try await panel.save(folder, "02-panel-on.png")
            try await darkPanel.save(folder, "02-panel-on-dark.png")
        }
        await step("volume within limit") {
            controller.setVolume(28)
            try await settle()
            try expect(speaker.volume == 28, "volume 28, got \(speaker.volume)")
            controller.setVolume(60)
            try await settle()
            try expect(speaker.volume == 30, "capped at 30, got \(speaker.volume)")
            try await panel.save(folder, "03-panel-at-limit.png")
        }
        await step("approach from above") {
            model.approachFromAbove = true
            defer { model.approachFromAbove = false }
            controller.setVolume(10)
            try await settle()
            speaker.writes = []
            controller.setVolume(20)
            try await settle()
            try expect(speaker.writes == [22, 20], "writes 22 then 20, got \(speaker.writes)")
        }
        await step("input switch in progress") {
            speaker.switchDelay = .milliseconds(2500)
            defer { speaker.switchDelay = .zero }
            controller.selectSource("wifi")
            try await waitFor("switch to start") { speaker.switching }
            try await panel.save(folder, "04-panel-switching.png")
            try await waitFor("switch to finish") { !controller.isBusy }
            try expect(speaker.source == "wifi" && !speaker.muted, "wifi and unmuted")
        }
        await step("now playing") {
            try await waitFor("the track") { controller.showsPlayback }
            try expect(controller.playback.title == speaker.playing.title, "the demo track")
            try await panel.save(folder, "30-panel-now-playing.png")
            try await darkPanel.save(folder, "30-panel-now-playing-dark.png")
            controller.sendPlayback(.togglePause)
            try await settle()
            try expect(speaker.commands == [.togglePause] && controller.playback.status == .paused, "paused, got \(speaker.commands) and \(controller.playback.status)")
            try await panel.save(folder, "31-panel-now-playing-paused.png")
            model.showsNowPlaying = false
            try expect(!controller.showsPlayback, "hidden with the setting off")
            model.showsNowPlaying = true
            try await waitFor("the track again") { controller.showsPlayback }
            speaker.playing.status = .playing
        }
        await step("notice") {
            speaker.volume = 40
            speaker.muted = true
            await controller.refresh()
            controller.toggleMute()
            try await settle()
            try expect(speaker.muted && controller.notice != nil, "still muted with a notice")
            try await panel.save(folder, "05-panel-notice.png")
            speaker.volume = 20
            speaker.muted = false
            await controller.refresh()
            controller.clearNotice()
        }
        await step("power off") {
            controller.standby()
            try await settle()
            try expect(controller.snapshot?.power == .standby, "standby")
            try await panel.save(folder, "06-panel-standby.png")
        }
        await step("power on to input") {
            controller.wake(to: "usb")
            try await settle()
            try expect(controller.snapshot?.power == .on && speaker.source == "usb", "on with usb")
        }
        await step("offline") {
            speaker.offline = true
            // One missed poll is tolerated; the second shows the speaker as offline.
            await controller.refresh()
            try expect(controller.isOnline, "one missed poll keeps the state")
            await controller.refresh()
            if case .offline = controller.phase {} else { throw Failure(description: "expected offline") }
            try await panel.save(folder, "07-panel-offline.png")
            speaker.offline = false
            await controller.refresh()
            try expect(controller.isOnline, "back online")
        }
        await step("volume shortcuts") {
            let keys = VolumeKeys(model: model)
            controller.setVolume(20)
            try await settle()
            keys.press(.volumeUp)
            try await settle()
            try expect(speaker.volume == 21, "21 after one press, got \(speaker.volume)")
            model.volumeKeyStep = 5
            keys.press(.volumeUp)
            keys.press(.volumeUp)
            try await settle()
            try expect(speaker.volume == 30, "stops at the limit 30, got \(speaker.volume)")
            keys.press(.volumeDown)
            try await settle()
            try expect(speaker.volume == 25, "25 after one press down, got \(speaker.volume)")
            model.volumeKeyStep = 1
            keys.press(.toggleMute)
            try await settle()
            try expect(speaker.muted, "muted")
            let state = HUDState()
            try await Capture(width: HUDView.size.width) { HUDView(model: model, state: state) }.save(folder, "33-volume-hud-muted.png")
            keys.press(.volumeUp)
            try await settle()
            try expect(!speaker.muted && speaker.volume == 26, "volume up unmutes and raises, got \(speaker.muted) \(speaker.volume)")
            try await Capture(width: HUDView.size.width) { HUDView(model: model, state: state) }.save(folder, "33-volume-hud.png")
            try await Capture(width: HUDView.size.width, appearance: .darkAqua) { HUDView(model: model, state: state) }.save(folder, "33-volume-hud-dark.png")
            state.hovering = true
            try await Capture(width: HUDView.size.width, appearance: .darkAqua) { HUDView(model: model, state: state) }.save(folder, "33-volume-hud-hover-dark.png")
            state.hovering = false
            state.content = .limit
            try await Capture(width: HUDView.size.width, appearance: .darkAqua) { HUDView(model: model, state: state) }.save(folder, "33-volume-hud-limit-dark.png")
            controller.setVolume(20)
            try await settle()
        }
        await step("shortcut recorder rules") {
            let command = Shortcut.recorded(keyCode: UInt16(kVK_ANSI_K), modifiers: [.option, .command], characters: "k")
            try expect((try? command.get())?.display == "⌥⌘K", "⌥⌘K")
            if case .success = Shortcut.recorded(keyCode: UInt16(kVK_ANSI_K), modifiers: [.shift], characters: "K") {
                throw Failure(description: "expected ⇧K alone to be refused")
            }
            try expect((try? Shortcut.recorded(keyCode: UInt16(kVK_F13), modifiers: [], characters: nil).get())?.display == "F13", "F13 alone")
            try expect(Shortcut.defaults[.volumeUp]?.display == "⌃⌥⌘↑", "default ⌃⌥⌘↑")
            try await Capture(width: 500) { ShortcutSettings().environment(model) }.save(folder, "32-settings-shortcuts.png")
        }
        await step("speaker finish") {
            model.speakerFinishID = "lsx-ii-lt/sage-green"
            try expect(model.speakerFinish?.name == "Sage Green", "finish of the connected LSX II LT")
            try await panel.save(folder, "12-panel-finish.png")
            try await darkPanel.save(folder, "12-panel-finish-dark.png")
        }
        await step("settings") {
            try await Capture(width: 500) { GeneralSettings().environment(model) }.save(folder, "08-settings-general.png")
            try await Capture(width: 500) { SpeakerSettings().environment(model) }.save(folder, "09-settings-speaker.png")
        }
        await step("unknown model") {
            speaker.model = "KEF NEWMODEL"
            speaker.firmware = "NEWMODEL_V0100"
            model.connect()
            try await waitFor("the unknown model") { controller.device?.model == "KEF NEWMODEL" && controller.isOnline }
            try expect(model.shownModel == "KEF NEWMODEL" && model.speakerFinish == nil, "the app icon's drawing and colors")
            try await Capture(width: 500) { SpeakerSettings().environment(model) }.save(folder, "13-settings-unknown-model.png")
        }
        for (index, name) in KefModels.names.enumerated() {
            await step("picked model \(name)") {
                model.pickedModel = name
                model.speakerFinishID = KefFinish.finishes(for: name).first?.id
                try expect(model.shownModel == name && model.speakerFinish?.model == name, "drawn as \(name)")
                try expect(controller.device?.sources.count == 7, "every input, as for any model the app does not know")
                let slug = name.lowercased().replacingOccurrences(of: " ", with: "-")
                try await panel.save(folder, "\(14 + index)-panel-\(slug).png")
            }
        }
        await step("back to the LSX II LT") {
            speaker.model = "LSX II LT"
            speaker.firmware = "LSXIILT_V2186"
            model.pickedModel = nil
            model.speakerFinishID = "lsx-ii-lt/sage-green"
            model.connect()
            try await waitFor("the LSX II LT") { controller.device?.model == "LSX II LT" && controller.isOnline && controller.snapshot != nil }
            let desk = model.speakers[0]
            try expect(desk.entry.name == controller.device?.name && desk.entry.model == "LSX II LT", "name and model kept for the list")
        }

        // Several speakers: the desk, the living room (on), the bedroom (standby) and the study (offline).
        await step("add speakers") {
            for address in [DemoSpeaker.livingAddress, DemoSpeaker.bedroomAddress, DemoSpeaker.studyAddress] {
                guard case .success = model.addSpeaker(host: address, password: "") else { throw Failure(description: "added \(address)") }
            }
            if case .success = model.addSpeaker(host: " \(DemoSpeaker.livingAddress) ", password: "") {
                throw Failure(description: "expected the same address twice to be refused")
            }
            try expect(model.speakers.count == 4 && model.hasSeveral, "four speakers")
            try expect(model.selected?.entry.host == DemoSpeaker.deskAddress, "the desk stays selected")
            let living = model.speakers[1], bedroom = model.speakers[2], study = model.speakers[3]
            try expect(!living.controller.listensForChanges && !bedroom.controller.listensForChanges, "only the selected speaker listens")
            model.update(living) { $0.finish = "ls60-wireless/royal-blue" }
            model.update(bedroom) { $0.finish = "ls50-wireless-ii/mineral-white" }
            try await waitFor("the other rooms read", timeout: .seconds(15)) {
                living.isOn && bedroom.controller.snapshot?.power == .standby && study.unreachable
            }
            try expect(living.listStatus == loc("음량 18", "Volume 18", "音量 18"), "living room at 18, got \(living.listStatus)")
            try expect(study.listStatus == SpeakerSlot.notConnected, "study not connected, got \(study.listStatus)")
            try expect(living.entry.name == room[DemoSpeaker.livingAddress]!.name, "the living room's name kept")
        }
        await step("panel with several speakers") {
            try await panel.save(folder, "40-panel-several.png")
            try await darkPanel.save(folder, "40-panel-several-dark.png")
        }
        await step("speaker list") {
            model.speakerListOpen = true
            await model.refreshOthers()
            try await Capture(width: 260) { SpeakerListPopover {}.environment(model) }.save(folder, "41-speaker-list.png")
            try await Capture(width: 260, appearance: .darkAqua) { SpeakerListPopover {}.environment(model) }.save(folder, "41-speaker-list-dark.png")
            model.speakerListOpen = false
        }
        await step("power from the list") {
            let bedroom = model.speakers[2]
            await model.setPower(bedroom, on: true)
            try await waitFor("the bedroom on") { room[DemoSpeaker.bedroomAddress]!.on && !bedroom.controller.isBusy }
            try expect(model.selected?.entry.host == DemoSpeaker.deskAddress, "the selection stays")
            await model.setPower(bedroom, on: false)
            try await waitFor("the bedroom off") { !room[DemoSpeaker.bedroomAddress]!.on && !bedroom.controller.isBusy }
        }
        await step("select another speaker") {
            let desk = model.speakers[0], living = model.speakers[1]
            model.select(living)
            try expect(model.selected === living && controller === living.controller, "the living room selected")
            try expect(living.controller.listensForChanges && !desk.controller.listensForChanges, "listening moves with the selection")
            try await waitFor("the living room read") { controller.isOnline && controller.snapshot?.volume == 18 }
            try await panel.save(folder, "43-panel-living-room.png")
            try await darkPanel.save(folder, "43-panel-living-room-dark.png")
            let state = HUDState()
            try await Capture(width: HUDView.size.width, appearance: .darkAqua) { HUDView(model: model, state: state) }.save(folder, "44-volume-hud-speaker-dark.png")
        }
        await step("limit per speaker") {
            model.appLimit = 20
            controller.setVolume(60)
            try await settle()
            try expect(room[DemoSpeaker.livingAddress]!.volume == 20, "living room capped at its own 20, got \(room[DemoSpeaker.livingAddress]!.volume)")
            try expect(model.speakers[0].entry.appLimit == VolumePolicy.defaultAppLimit, "the desk keeps its limit")
        }
        await step("next speaker shortcut") {
            try expect(model.shortcuts[.nextSpeaker] == nil, "off by default")
            let keys = VolumeKeys(model: model)
            keys.press(.nextSpeaker)
            try expect(model.selected?.entry.host == DemoSpeaker.bedroomAddress, "to the bedroom")
            try await waitFor("the bedroom read") { controller.isOnline && controller.snapshot != nil }
            let state = HUDState()
            state.content = .speaker
            try await Capture(width: HUDView.size.width, appearance: .darkAqua) { HUDView(model: model, state: state) }.save(folder, "45-volume-hud-next-standby-dark.png")
            keys.press(.nextSpeaker)
            try expect(model.selected?.entry.host == DemoSpeaker.studyAddress, "to the study")
            keys.press(.nextSpeaker)
            try expect(model.selected?.entry.host == DemoSpeaker.deskAddress, "around to the desk")
            try await Capture(width: 500) { ShortcutSettings().environment(model) }.save(folder, "46-settings-shortcuts.png")
        }
        await step("settings with several speakers") {
            try await Capture(width: 500) { SpeakerSettings().environment(model) }.save(folder, "47-settings-speakers.png")
            try await Capture(width: 500, appearance: .darkAqua) { SpeakerSettings().environment(model) }.save(folder, "47-settings-speakers-dark.png")
            let living = model.speakers[1]
            try await Capture(width: 500, appearance: .darkAqua) { SpeakerSheet(slot: living) {}.environment(model) }.save(folder, "48-settings-speaker-page-dark.png")
        }
        await step("turn all off") {
            await model.turnAllOff()
            try await waitFor("all off") {
                !room[DemoSpeaker.deskAddress]!.on && !room[DemoSpeaker.livingAddress]!.on && model.speakers.allSatisfy { !$0.controller.isBusy }
            }
            try expect(model.selected?.entry.host == DemoSpeaker.deskAddress, "the selection stays")
        }
        await step("remove speakers") {
            let living = model.speakers[1]
            model.select(living)
            model.remove(model.speakers[3])
            model.remove(living)
            try expect(model.selected?.entry.host == DemoSpeaker.deskAddress, "the one before takes over")
            try expect(controller.listensForChanges, "and listens")
            model.remove(model.speakers[1])
            try expect(model.speakers.count == 1 && !model.hasSeveral, "one speaker again")
            controller.wake(to: "usb")
            try await settle()
            try await panel.save(folder, "49-panel-one-again.png")
        }
        await step("menu bar icons") {
            try saveMenuBarStrip(folder.appending(path: "11-menu-bar.png"))
        }

        report.append(failures == 0 ? "OK" : "\(failures) FAILED")
        try? (report.joined(separator: "\n") + "\n").write(to: folder.appending(path: "report.txt"), atomically: true, encoding: .utf8)
        UserDefaults.standard.removePersistentDomain(forName: suite)
        return failures == 0
    }

    /// The volume at 25 and 9 to show the fixed number width, then muted, standby,
    /// offline and connecting, on dark and light bars.
    private static func saveMenuBarStrip(_ url: URL) throws {
        let items: [NSImage] = [
            MenuBarGlyph.label(.on, volume: 25), MenuBarGlyph.label(.on, volume: 9),
            MenuBarGlyph.muted.image, MenuBarGlyph.standby.image, MenuBarGlyph.offline.image,
            MenuBarGlyph.connecting,
        ]
        let scale: CGFloat = 2, gap: CGFloat = 14, rowHeight: CGFloat = 24
        let width = items.reduce(gap) { $0 + $1.size.width + gap }
        let size = CGSize(width: width * scale, height: rowHeight * 2 * scale)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        ), let context = NSGraphicsContext(bitmapImageRep: rep) else { throw Failure(description: "bitmap") }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = context
        context.cgContext.scaleBy(x: scale, y: scale)
        for (row, (background, ink)) in [(NSColor(white: 0.12, alpha: 1), NSColor.white), (NSColor(white: 0.93, alpha: 1), NSColor.black)].enumerated() {
            let y = CGFloat(row) * rowHeight
            background.setFill()
            CGRect(x: 0, y: y, width: width, height: rowHeight).fill()
            var x = gap
            for item in items {
                let tinted = MenuBarGlyph.render(size: item.size, template: false) { _ in
                    let rect = CGRect(origin: .zero, size: item.size)
                    item.draw(in: rect)
                    ink.set()
                    rect.fill(using: .sourceAtop)
                }
                tinted.draw(in: CGRect(x: x, y: y + (rowHeight - item.size.height) / 2, width: item.size.width, height: item.size.height))
                x += item.size.width + gap
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        guard let data = rep.representation(using: .png, properties: [:]) else { throw Failure(description: "png") }
        try data.write(to: url)
    }
}

/// Saves a SwiftUI view as a PNG through an offscreen window. Each capture gets
/// a fresh window, so controls show the current state rather than the end of an
/// animation from the previous one.
@MainActor
private final class Capture {
    private let width: CGFloat
    private let appearance: NSAppearance.Name
    private let makeView: () -> AnyView

    init<Content: View>(width: CGFloat, appearance: NSAppearance.Name = .aqua, @ViewBuilder content: @escaping () -> Content) {
        self.width = width
        self.appearance = appearance
        makeView = { AnyView(content()) }
    }

    func save(_ folder: URL, _ name: String) async throws {
        let host = NSHostingView(rootView: makeView()
            .frame(width: width)
            .background(Color(nsColor: .windowBackgroundColor)))
        let window = NSWindow(
            contentRect: CGRect(x: -4000, y: -4000, width: width, height: 400),
            styleMask: [.borderless], backing: .buffered, defer: false
        )
        window.appearance = NSAppearance(named: appearance)
        window.isReleasedWhenClosed = false
        window.contentView = host
        window.orderFrontRegardless()
        defer { window.orderOut(nil) }

        // Let SwiftUI lay out and apply the current state.
        try await Task.sleep(for: .milliseconds(450))
        window.setContentSize(host.fittingSize)
        host.layoutSubtreeIfNeeded()
        host.display()
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else {
            throw SelfTest.Failure(description: "no bitmap for \(name)")
        }
        host.cacheDisplay(in: host.bounds, to: rep)
        guard let data = rep.representation(using: .png, properties: [:]) else {
            throw SelfTest.Failure(description: "no png for \(name)")
        }
        try data.write(to: folder.appending(path: name))
    }
}

/// In-memory speaker, an LSX II LT unless told otherwise. Power, inputs and volume behave like the real one,
/// instantly. The model it reports can be changed to one the app does not know.
@MainActor
final class DemoSpeaker {
    static let deskAddress = "192.168.0.20", livingAddress = "192.168.0.21", bedroomAddress = "192.168.0.22", studyAddress = "192.168.0.23"

    /// The rooms of the self-test and `--demo --speakers`, by address, as on Windows.
    static func room() -> [String: DemoSpeaker] {
        let living = DemoSpeaker(), bedroom = DemoSpeaker(), study = DemoSpeaker()
        living.name = loc("거실 스피커", "Living room", "リビングのスピーカー")
        living.model = "LS60 Wireless"
        living.firmware = "LS60_V2130"
        living.source = "wifi"
        living.volume = 18
        bedroom.name = loc("침실 스피커", "Bedroom", "寝室のスピーカー")
        bedroom.model = "LS50 Wireless II"
        bedroom.firmware = "LS50W2_V2130"
        bedroom.on = false
        bedroom.source = "tv"
        bedroom.volume = 15
        study.name = loc("서재 스피커", "Study", "書斎のスピーカー")
        study.model = "LSX II"
        study.firmware = "LSXII_V2130"
        study.offline = true
        return [deskAddress: DemoSpeaker(), livingAddress: living, bedroomAddress: bedroom, studyAddress: study]
    }

    var name = loc("책상 스피커", "Desk speakers", "デスクのスピーカー")
    var model = "LSX II LT"
    var firmware = "LSXIILT_V2186"
    var on = true
    var source = "usb"
    var volume = 25
    var muted = false
    var offline = false
    var switchDelay: Duration = .zero
    var switching = false
    var writes: [Int] = []
    var playing = PlaybackInfo(
        status: .playing,
        title: loc("조용한 오후", "Quiet Afternoon", "静かな午後"), artist: "Remote for KEF", album: "",
        service: "AirPlay", durationMs: 214_000, positionMs: 83_000,
        canTogglePause: true, canNext: true, canPrevious: true
    )
    var commands: [PlaybackCommand] = []
}

@MainActor
final class DemoDriver: SpeakerDriver {
    private let speaker: DemoSpeaker

    init(_ speaker: DemoSpeaker) {
        self.speaker = speaker
    }

    private struct Offline: Error, CustomStringConvertible {
        var description: String { loc("네트워크 연결 실패", "Network connection failed", "ネットワークに接続できませんでした") }
    }

    private func online() throws {
        if speaker.offline { throw Offline() }
    }

    func connect() async throws -> DeviceInfo {
        try online()
        let known = KefModels.names.contains(speaker.model)
        var sources = [
            SourceOption(id: "usb", name: "USB-C / PC"), SourceOption(id: "wifi", name: "Wi-Fi"),
            SourceOption(id: "bluetooth", name: "Bluetooth"), SourceOption(id: "tv", name: "TV (HDMI eARC)"),
            SourceOption(id: "optical", name: "Optical"),
        ]
        if !known {
            sources += [SourceOption(id: "coaxial", name: "Coaxial"), SourceOption(id: "analog", name: "Analog")]
        }
        return DeviceInfo(
            name: speaker.name, model: speaker.model, firmware: speaker.firmware,
            capabilities: [.volume, .mute, .source, .power, .playback],
            sources: sources,
            deviceVolumeLimit: nil, verifiedModel: speaker.model == "LSX II LT"
        )
    }

    func read() async throws -> SpeakerSnapshot {
        try online()
        return SpeakerSnapshot(power: speaker.on ? .on : .standby, sourceId: speaker.on ? speaker.source : nil, volume: speaker.volume, muted: speaker.muted)
    }

    func setVolume(_ volume: Int) async throws {
        try online()
        speaker.volume = volume
        speaker.writes.append(volume)
    }

    func setMute(_ muted: Bool) async throws {
        try online()
        speaker.muted = muted
    }

    func setSource(_ sourceId: String) async throws {
        try online()
        if speaker.switchDelay > .zero {
            speaker.switching = true
            defer { speaker.switching = false }
            try await Task.sleep(for: speaker.switchDelay)
        }
        speaker.source = sourceId
    }

    func setPower(_ on: Bool) async throws {
        try online()
        speaker.on = on
    }

    /// Like the KEF driver: only the streaming inputs have something playing.
    nonisolated func sourceHasPlayback(_ sourceId: String?) -> Bool {
        sourceId == "wifi" || sourceId == "bluetooth"
    }

    func readPlayback() async throws -> PlaybackInfo {
        try online()
        return speaker.playing
    }

    func sendPlayback(_ command: PlaybackCommand) async throws {
        try online()
        speaker.commands.append(command)
        if command == .togglePause {
            speaker.playing.status = speaker.playing.status == .playing ? .paused : .playing
        }
    }

    nonisolated func approachOvershoot(current: Int, target: Int) -> Int? {
        target > current ? KefVolumeCurve.overshoot(for: target) : nil
    }
}
