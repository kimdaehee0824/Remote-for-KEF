import Observation
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
    /// Reads that fail right after a power change, like a speaker restarting its network.
    var failuresAfterPower = 0
    private var failingReads = 0
    /// Reads that show the input back but the power status still in standby, like the W2 firmware waking.
    var wakingReads = 0
    var wakingReadsAfterPower = 0
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
            capabilities: DriverCapabilities([.volume, .mute, .source, .power]).union(extraCapabilities),
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
        if failingReads > 0 {
            failingReads -= 1
            throw FakeError.offline
        }
        let waking = power == .on && wakingReads > 0
        if waking { wakingReads -= 1 }
        return SpeakerSnapshot(power: waking ? .standby : power, sourceId: power == .on ? source : nil, volume: volume, muted: muted)
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
        guard !ignoreWrites else { return }
        power = on ? .on : .standby
        failingReads = failuresAfterPower
        if on { wakingReads = wakingReadsAfterPower }
    }

    nonisolated func approachOvershoot(current: Int, target: Int) -> Int? {
        target > current && target < 41 ? target + 2 : nil
    }

    // Playback and change reports (SpeakerControllerChangesTests).
    var extraCapabilities: DriverCapabilities = []
    var playing = PlaybackInfo.none
    var playbackReads = 0
    var playbackCommands: [PlaybackCommand] = []
    var rejectPlayback = false
    /// Reads after a command that still show the old player state.
    var playbackLagReads = 0
    private var pendingPlayback: PlaybackInfo?
    var changeCalls = 0
    private var changeWaits: [CheckedContinuation<DeviceChanges, any Error>] = []
    var waitingForChanges: Bool { !changeWaits.isEmpty }

    nonisolated func sourceHasPlayback(_ sourceId: String?) -> Bool { sourceId == "wifi" }

    func readPlayback() async throws -> PlaybackInfo {
        playbackReads += 1
        if broken { throw FakeError.offline }
        if let pending = pendingPlayback {
            if playbackLagReads > 0 {
                playbackLagReads -= 1
            } else {
                playing = pending
                pendingPlayback = nil
            }
        }
        return playing
    }

    func sendPlayback(_ command: PlaybackCommand) async throws {
        if rejectPlayback { throw FakeError.offline }
        playbackCommands.append(command)
        var next = playing
        switch command {
        case .togglePause: next.status = playing.status == .playing ? .paused : .playing
        case .next, .previous:
            next.title += " (\(command.rawValue))"
            next.positionMs = 0
        }
        if playbackLagReads > 0 { pendingPlayback = next } else { playing = next }
    }

    func waitForChanges() async throws -> DeviceChanges {
        changeCalls += 1
        return try await withCheckedThrowingContinuation { changeWaits.append($0) }
    }

    func report(_ changes: DeviceChanges) { changeWaits.removeFirst().resume(returning: changes) }
    func failChanges() { changeWaits.removeFirst().resume(throwing: FakeError.offline) }
}

enum FakeError: Error { case offline }

/// Set from observation's change handler.
final class ChangeFlag: @unchecked Sendable {
    var value = false
}

/// Pauses the controller asked for, in order.
final class SleepLog: @unchecked Sendable {
    var durations: [Duration] = []
}

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

    @Test func deviceLimitIsReadAgain() async {
        let driver = FakeDriver()
        let controller = await connected(driver, limit: 40)
        #expect(!controller.limitFromDevice)
        driver.deviceLimit = 25
        await controller.refreshDevice()
        #expect(controller.effectiveLimit == 25)
        #expect(controller.limitFromDevice)
        #expect(driver.writes.isEmpty)
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
        #expect(driver.writes == ["standby", "power=on"])
        #expect(controller.snapshot?.power == .on)
    }

    @Test func wakeToAnotherInputSwitchesMuted() async {
        let driver = FakeDriver()
        driver.power = .standby
        let controller = await connected(driver)
        controller.wake(to: "wifi")
        await settle(controller)
        #expect(driver.writes == ["power=on", "mute=true", "source=wifi", "mute=false"])
        #expect(controller.snapshot?.sourceId == "wifi")
        #expect(controller.snapshot?.muted == false)
    }

    @Test func wakeWithoutInputKeepsLastInput() async {
        let driver = FakeDriver()
        driver.power = .standby
        let controller = await connected(driver)
        controller.wake()
        await settle(controller)
        #expect(driver.writes == ["power=on"])
        #expect(controller.snapshot?.sourceId == "usb")
    }

    @Test func wakeWaitsForThePowerStatus() async {
        let driver = FakeDriver()
        driver.power = .standby
        driver.wakingReadsAfterPower = 3
        let controller = await connected(driver)
        controller.wake()
        await settle(controller)
        #expect(driver.writes == ["power=on"])
        #expect(controller.snapshot?.power == .on)
        #expect(controller.snapshot?.sourceId == "usb")
        #expect(!controller.isWaking)
    }

    @Test func wakingElsewhereShowsTheInputFirst() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        controller.standby()
        await settle(controller)
        // Woken with the remote: the input is back, the power status follows a moment later.
        driver.power = .on
        driver.wakingReads = 1
        await controller.refresh()
        #expect(controller.snapshot?.power == .standby)
        #expect(controller.snapshot?.sourceId == "usb")
        #expect(controller.isWaking)
        await controller.refresh()
        #expect(controller.snapshot?.power == .on)
        #expect(!controller.isWaking)
    }

    @Test func wakeWaitsForASlowSpeaker() async {
        let driver = FakeDriver()
        driver.power = .standby
        // Past the 20 reads the old wait of about 6 s allowed.
        driver.wakingReadsAfterPower = 34
        let controller = await connected(driver)
        controller.wake()
        await settle(controller)
        #expect(driver.writes == ["power=on"])
        #expect(controller.snapshot?.power == .on)
        #expect(controller.notice == nil)
    }

    @Test func wakeReadsAtMostTwiceASecond() async {
        let driver = FakeDriver()
        driver.power = .standby
        driver.ignoreWrites = true
        let log = SleepLog()
        let controller = SpeakerController(sleep: { log.durations.append($0) })
        await controller.connect(driver)
        controller.wake()
        await settle(controller)
        #expect(log.durations.allSatisfy { $0 >= .milliseconds(500) })
        let total = log.durations.reduce(Duration.zero, +)
        #expect(total >= .seconds(28) && total <= .seconds(32))
    }

    @Test func stillWakingAfterTheWaitIsNotAFailure() async {
        let driver = FakeDriver()
        driver.power = .standby
        driver.wakingReadsAfterPower = 1000
        let controller = await connected(driver)
        controller.wake()
        await settle(controller)
        #expect(driver.writes == ["power=on"])
        #expect(controller.phase == .online)
        #expect(controller.isWaking)
        #expect(controller.notice != nil)
        // The speaker gets there in the end: shown as on, and the notice goes.
        driver.wakingReads = 0
        await controller.refresh()
        #expect(controller.snapshot?.power == .on)
        #expect(controller.notice == nil)
        #expect(driver.writes == ["power=on"])
    }

    @Test func unconfirmedWakeIsReportedAndClearedOnceOn() async {
        let driver = FakeDriver()
        driver.power = .standby
        driver.ignoreWrites = true
        let controller = await connected(driver)
        controller.wake()
        await settle(controller)
        #expect(driver.writes == ["power=on"])
        #expect(controller.phase == .online)
        #expect(controller.snapshot?.power == .standby)
        #expect(controller.notice != nil)
        // Turned on later, with the remote or by the speaker itself.
        driver.power = .on
        await controller.refresh()
        #expect(controller.snapshot?.power == .on)
        #expect(controller.notice == nil)
    }

    @Test func lateWakeSaysTheInputWasNotSwitched() async {
        let driver = FakeDriver()
        driver.power = .standby
        driver.wakingReadsAfterPower = 1000
        let controller = await connected(driver)
        controller.wake(to: "wifi")
        await settle(controller)
        driver.wakingReads = 0
        await controller.refresh()
        #expect(controller.snapshot?.power == .on)
        #expect(controller.snapshot?.sourceId == "usb")
        #expect(controller.notice != nil)
        #expect(driver.writes == ["power=on"])
    }

    @Test func powerChangesTolerateTransientReadErrors() async {
        let driver = FakeDriver()
        driver.failuresAfterPower = 2
        let controller = await connected(driver)
        controller.standby()
        await settle(controller)
        #expect(controller.snapshot?.power == .standby)
        controller.wake()
        await settle(controller)
        #expect(controller.phase == .online)
        #expect(controller.snapshot?.power == .on)
        #expect(driver.writes == ["standby", "power=on"])
    }

    @Test func approachFromAboveWritesOvershootFirst() async {
        let driver = FakeDriver()
        driver.volume = 10
        let controller = await connected(driver)
        controller.approachFromAbove = true
        controller.setVolume(20)
        await settle(controller)
        #expect(driver.writes == ["volume=22", "volume=20"])
        #expect(controller.snapshot?.volume == 20)
    }

    @Test func overshootIsCappedAtLimit() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        controller.approachFromAbove = true
        controller.setVolume(29)
        await settle(controller)
        #expect(driver.writes == ["volume=30", "volume=29"])
        driver.writes = []
        controller.setVolume(30)
        await settle(controller)
        #expect(driver.writes == ["volume=30"])
    }

    @Test func loweringNeverOvershoots() async {
        let driver = FakeDriver()
        driver.volume = 25
        let controller = await connected(driver)
        controller.approachFromAbove = true
        controller.setVolume(20)
        await settle(controller)
        #expect(driver.writes == ["volume=20"])
    }

    @Test func activityShowsTheCommandInProgress() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.holdNextRead = true
        controller.selectSource("wifi")
        await waitForHeldRead(driver)
        #expect(controller.activity == .source("wifi"))
        driver.release()
        await settle(controller)
        #expect(controller.activity == nil)
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

    @Test func oneMissedPollKeepsTheState() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        driver.broken = true
        #expect(await controller.refresh() == false)
        #expect(controller.phase == .online)
        #expect(controller.snapshot?.volume == 20)
        driver.broken = false
        #expect(await controller.refresh())
        // The count starts over after a good read: one more miss is still tolerated.
        driver.broken = true
        await controller.refresh()
        #expect(controller.phase == .online)
        await controller.refresh()
        if case .offline = controller.phase {} else { Issue.record("expected offline after two missed polls") }
        #expect(driver.writes.isEmpty)
    }

    @Test func startupRetriesBeforeShowingOffline() async {
        let driver = FakeDriver()
        driver.broken = true
        let controller = await connected(driver)
        #expect(controller.phase == .connecting)
        await controller.refresh()
        await controller.refresh()
        #expect(controller.phase == .connecting)
        await controller.refresh()
        if case .offline = controller.phase {} else { Issue.record("expected offline after \(controller.connectAttempts) attempts") }
        driver.broken = false
        await controller.refresh()
        #expect(controller.phase == .online)
        #expect(driver.writes.isEmpty)
    }

    @Test func wakeFromSleepKeepsTheLastStateWhileRetrying() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        controller.suspend()
        controller.resume()
        driver.broken = true
        await controller.refresh()
        #expect(controller.phase == .connecting)
        #expect(controller.snapshot?.volume == 20)
        driver.broken = false
        await controller.refresh()
        #expect(controller.phase == .online)
    }

    @Test func unchangedPollsDoNotRedraw() async {
        let driver = FakeDriver()
        let controller = await connected(driver)
        let changed = ChangeFlag()
        func watch() {
            changed.value = false
            withObservationTracking {
                _ = controller.phase
                _ = controller.snapshot
                _ = controller.device
                _ = controller.lastActiveSource
            } onChange: {
                changed.value = true
            }
        }
        watch()
        await controller.refresh()
        await controller.refreshDevice()
        #expect(!changed.value)
        driver.volume = 21
        watch()
        await controller.refresh()
        #expect(changed.value)
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
