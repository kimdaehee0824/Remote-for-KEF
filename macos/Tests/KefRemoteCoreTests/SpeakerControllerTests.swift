import Testing
@testable import KefRemoteCore

@MainActor
final class FakeDriver: SpeakerDriver {
    var power: PowerState = .on
    var source = "usb"
    var volumes: [String: Int] = ["usb": 20, "wifi": 20]
    var muted = false
    var broken = false
    var ignoreWrites = false
    var resetMuteOnSource = false
    var writes: [String] = []
    var reads = 0
    /// Suspends the next read until `release()` is called.
    var holdNextRead = false
    private var held: CheckedContinuation<Void, Never>?

    var volume: Int {
        get { volumes[source, default: 0] }
        set { volumes[source] = newValue }
    }

    func release() {
        held?.resume()
        held = nil
    }

    var isHolding: Bool { held != nil }

    func connect() async throws -> DeviceInfo {
        if broken { throw FakeError.offline }
        return DeviceInfo(
            name: "Test", model: "Fake", firmware: "1",
            capabilities: [.volume, .mute, .source, .power],
            sources: [SourceOption(id: "usb", name: "USB"), SourceOption(id: "wifi", name: "Wi-Fi")],
            deviceVolumeLimit: deviceLimit, verifiedModel: true
        )
    }

    var deviceLimit: Int?

    func read() async throws -> SpeakerSnapshot {
        reads += 1
        if holdNextRead {
            holdNextRead = false
            await withCheckedContinuation { held = $0 }
        }
        if broken { throw FakeError.offline }
        return SpeakerSnapshot(power: power, sourceId: power == .on ? source : nil, volume: volume, muted: muted)
    }

    func setVolume(_ volume: Int) async throws {
        writes.append("volume=\(volume)")
        if !ignoreWrites { self.volume = volume }
    }

    func setMute(_ muted: Bool) async throws {
        writes.append("mute=\(muted)")
        if !ignoreWrites { self.muted = muted }
    }

    func setSource(_ sourceId: String) async throws {
        writes.append("source=\(sourceId)")
        source = sourceId
        power = .on
        if resetMuteOnSource { muted = false }
    }

    func setPower(_ on: Bool) async throws {
        writes.append(on ? "power=on" : "standby")
        power = on ? .on : .standby
    }
}

enum FakeError: Error { case offline }

@MainActor
func connected(_ driver: FakeDriver, limit: Int = 30) async -> SpeakerController {
    let controller = SpeakerController(sleep: { _ in })
    controller.appLimit = limit
    await controller.connect(driver)
    return controller
}

@MainActor
func settle(_ controller: SpeakerController) async {
    while let task = controller.drainTask { await task.value }
}

@MainActor
func waitForHeldRead(_ driver: FakeDriver) async {
    while !driver.isHolding { await Task.yield() }
}

@MainActor
struct SpeakerControllerTests {
    @Test func startupIsReadOnly() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        #expect(controller.phase == .online)
        #expect(controller.snapshot?.volume == 20)
        #expect(driver.writes.isEmpty)
    }

    @Test func startupAboveLimitIsShownAsIs() async {
        let driver = FakeDriver()
        driver.volume = 32
        let controller = await connected(driver)
        #expect(controller.snapshot?.volume == 32)
        #expect(controller.isAboveLimit)
        #expect(driver.writes.isEmpty)
    }

    @Test func oneStepDownIsVerified() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        controller.stepVolume(-1)
        await settle(controller)
        #expect(driver.writes == ["volume=19"])
        #expect(controller.snapshot?.volume == 19)
        #expect(controller.phase == .online)
    }

    @Test func stepAtLimitSendsNoWrite() async {
        let driver = FakeDriver()
        driver.volume = 30
        let controller = await connected(driver)
        controller.stepVolume(1)
        await settle(controller)
        #expect(driver.writes.isEmpty)
        #expect(controller.notice != nil)
    }

    @Test func sliderRaiseStopsAtLimit() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        controller.setVolume(55)
        await settle(controller)
        #expect(driver.writes == ["volume=30"])
    }

    @Test func aboveLimitCanOnlyGoDown() async {
        let driver = FakeDriver()
        driver.volume = 70
        let controller = await connected(driver)
        controller.setVolume(80)
        await settle(controller)
        #expect(driver.writes.isEmpty)
        controller.setVolume(60)
        await settle(controller)
        #expect(driver.writes == ["volume=60"])
    }

    @Test func deviceLimitBelowAppLimitApplies() async {
        let driver = FakeDriver()
        driver.deviceLimit = 25
        let controller = await connected(driver, limit: 40)
        #expect(controller.effectiveLimit == 25)
        controller.setVolume(35)
        await settle(controller)
        #expect(driver.writes == ["volume=25"])
    }

    @Test func zeroFloor() {
        #expect(VolumePolicy.target(requested: -5, current: 0, limit: 30) == 0)
        #expect(VolumePolicy.target(requested: 31, current: 29, limit: 30) == 30)
        #expect(VolumePolicy.target(requested: 71, current: 70, limit: 30) == 70)
        #expect(VolumePolicy.target(requested: 69, current: 70, limit: 30) == 69)
    }

    @Test func disconnectDropsCommandWithoutWrite() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.broken = true
        controller.stepVolume(-1)
        await settle(controller)
        #expect(driver.writes.isEmpty)
        if case .offline = controller.phase {} else { Issue.record("expected offline") }
    }

    @Test func reconnectDoesNotReplay() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.broken = true
        controller.stepVolume(-1)
        await settle(controller)
        driver.broken = false
        await controller.refresh()
        await settle(controller)
        #expect(controller.phase == .online)
        #expect(driver.writes.isEmpty)
    }

    @Test func externalSourceChangeAbortsVolume() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.source = "wifi"
        controller.stepVolume(-1)
        await settle(controller)
        #expect(driver.writes.isEmpty)
        #expect(controller.notice != nil)
        #expect(controller.phase == .online)
    }

    @Test func unverifiedWriteIsNeverRetried() async {
        let driver = FakeDriver()
        driver.ignoreWrites = true
        let controller = await connected(driver)
        controller.stepVolume(-1)
        await settle(controller)
        #expect(driver.writes == ["volume=19"])
        if case .offline = controller.phase {} else { Issue.record("expected offline") }
    }

    @Test func busyVolumeInputCoalescesToLatest() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.holdNextRead = true
        controller.setVolume(21)
        await waitForHeldRead(driver)
        controller.setVolume(22)
        controller.setVolume(24)
        #expect(controller.pendingVolume == 24)
        driver.release()
        await settle(controller)
        #expect(driver.writes == ["volume=21", "volume=24"])
        #expect(controller.pendingVolume == nil)
    }

    @Test func busyStepsAreSummed() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.holdNextRead = true
        controller.stepVolume(-1)
        await waitForHeldRead(driver)
        controller.stepVolume(-1)
        controller.stepVolume(-1)
        driver.release()
        await settle(controller)
        #expect(driver.writes == ["volume=19", "volume=17"])
    }

    @Test func busyNonVolumeInputIsDropped() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.holdNextRead = true
        controller.setVolume(21)
        await waitForHeldRead(driver)
        controller.toggleMute()
        driver.release()
        await settle(controller)
        #expect(driver.writes == ["volume=21"])
    }

    @Test func disconnectDuringReadCancelsWrite() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.holdNextRead = true
        controller.setVolume(25)
        await waitForHeldRead(driver)
        controller.disconnect()
        driver.release()
        await settle(controller)
        #expect(driver.writes.isEmpty)
    }

    @Test func suspendCancelsPendingWrite() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.holdNextRead = true
        controller.setVolume(25)
        await waitForHeldRead(driver)
        controller.suspend()
        driver.release()
        await settle(controller)
        #expect(driver.writes.isEmpty)
    }

    @Test func switchMutesAndRestoresWhenNotLouder() async {
        let driver = FakeDriver()
        driver.volumes = ["usb": 20, "wifi": 18]
        let controller = await connected(driver)
        controller.selectSource("wifi")
        await settle(controller)
        #expect(driver.writes == ["mute=true", "source=wifi", "mute=false"])
        #expect(controller.snapshot?.sourceId == "wifi")
        #expect(controller.snapshot?.muted == false)
    }

    @Test func switchStaysMutedWhenNewInputIsLouder() async {
        let driver = FakeDriver()
        driver.volumes = ["usb": 20, "wifi": 26]
        let controller = await connected(driver)
        controller.selectSource("wifi")
        await settle(controller)
        #expect(driver.writes == ["mute=true", "source=wifi"])
        #expect(controller.snapshot?.muted == true)
        #expect(controller.notice != nil)
    }

    @Test func switchKeepsExistingMute() async {
        let driver = FakeDriver()
        driver.muted = true
        let controller = await connected(driver)
        controller.selectSource("wifi")
        await settle(controller)
        #expect(driver.writes == ["source=wifi"])
        #expect(driver.muted)
    }

    @Test func muteResetDuringSwitchFailsClosed() async {
        let driver = FakeDriver()
        driver.resetMuteOnSource = true
        let controller = await connected(driver)
        controller.selectSource("wifi")
        await settle(controller)
        #expect(driver.writes == ["mute=true", "source=wifi"])
        if case .offline = controller.phase {} else { Issue.record("expected offline") }
    }

    @Test func switchAboveLimitIsRefused() async {
        let driver = FakeDriver()
        driver.volume = 40
        let controller = await connected(driver)
        controller.selectSource("wifi")
        await settle(controller)
        #expect(driver.writes.isEmpty)
    }

    @Test func unmuteAboveLimitIsRefused() async {
        let driver = FakeDriver()
        driver.volume = 70
        driver.muted = true
        let controller = await connected(driver)
        controller.toggleMute()
        await settle(controller)
        #expect(driver.writes.isEmpty)
        #expect(controller.notice != nil)
    }

    @Test func muteToggleIsVerified() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        controller.toggleMute()
        await settle(controller)
        #expect(driver.writes == ["mute=true"])
        #expect(controller.snapshot?.muted == true)
    }

    @Test func standbyAndWake() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        controller.standby()
        await settle(controller)
        #expect(controller.snapshot?.power == .standby)
        #expect(controller.lastActiveSource == "usb")
        controller.wake(to: "usb")
        await settle(controller)
        #expect(driver.writes == ["standby", "source=usb"])
        #expect(controller.snapshot?.power == .on)
    }

    @Test func wakeRefusedAboveLimit() async {
        let driver = FakeDriver()
        driver.volume = 50
        driver.power = .standby
        let controller = await connected(driver)
        controller.wake(to: "usb")
        await settle(controller)
        #expect(driver.writes.isEmpty)
    }

    @Test func commandsWhileOfflineAreRefused() async {
        let driver = FakeDriver()
        driver.broken = true
        let controller = await connected(driver)
        controller.setVolume(10)
        await settle(controller)
        #expect(driver.writes.isEmpty)
        #expect(driver.reads == 0)
    }
}
