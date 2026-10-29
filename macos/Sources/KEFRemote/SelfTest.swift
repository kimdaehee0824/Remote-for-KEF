import AppKit
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
        let controller = model.controller
        let speaker = DemoSpeaker()
        let driver = DemoDriver(speaker)
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
            model.connect(using: driver)
            try await waitFor("online") { controller.isOnline && controller.snapshot != nil }
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
        await step("settings") {
            try await Capture(width: 500) { GeneralSettings().environment(model) }.save(folder, "08-settings-general.png")
            try await Capture(width: 500) { SpeakerSettings().environment(model) }.save(folder, "09-settings-speaker.png")
            try await Capture(width: 500) { VolumeSettings().environment(model) }.save(folder, "10-settings-volume.png")
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

/// In-memory LSX II LT. Power, inputs and volume behave like the real one, instantly.
@MainActor
final class DemoSpeaker {
    var on = true
    var source = "usb"
    var volume = 25
    var muted = false
    var offline = false
    var switchDelay: Duration = .zero
    var switching = false
    var writes: [Int] = []
}

@MainActor
final class DemoDriver: SpeakerDriver {
    private let speaker: DemoSpeaker

    init(_ speaker: DemoSpeaker) {
        self.speaker = speaker
    }

    private struct Offline: Error, CustomStringConvertible {
        var description: String { "네트워크 연결 실패" }
    }

    private func online() throws {
        if speaker.offline { throw Offline() }
    }

    func connect() async throws -> DeviceInfo {
        try online()
        return DeviceInfo(
            name: "책상 스피커", model: "LSX II LT", firmware: "LSXIILT_V2186",
            capabilities: [.volume, .mute, .source, .power],
            sources: [
                SourceOption(id: "usb", name: "USB-C / PC"), SourceOption(id: "wifi", name: "Wi-Fi"),
                SourceOption(id: "bluetooth", name: "Bluetooth"), SourceOption(id: "tv", name: "TV (HDMI eARC)"),
                SourceOption(id: "optical", name: "Optical"),
            ],
            deviceVolumeLimit: nil, verifiedModel: true
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

    nonisolated func approachOvershoot(current: Int, target: Int) -> Int? {
        target > current ? KefVolumeCurve.overshoot(for: target) : nil
    }
}
