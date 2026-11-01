import Testing
@testable import KefRemoteCore

/// Lets the controller's tasks run until `condition` holds.
@MainActor
func eventually(_ condition: () -> Bool) async {
    for _ in 0..<2000 where !condition() { await Task.yield() }
}

/// Change reports from the device and the now-playing player.
@MainActor
struct SpeakerControllerChangesTests {
    var now = 100.0

    static func song(_ status: PlaybackStatus = .playing) -> PlaybackInfo {
        PlaybackInfo(status: status, title: "Love Me Not", artist: "Ravyn Lenae", durationMs: 213_466, positionMs: 49_876,
                     canTogglePause: true, canNext: true, canPrevious: true)
    }

    func listening(_ driver: FakeDriver, playback: Bool = false) async -> SpeakerController {
        driver.extraCapabilities = [.changes, .playback]
        let controller = SpeakerController(sleep: { _ in })
        controller.appLimit = 30
        controller.playbackEnabled = playback
        await controller.connect(driver)
        await eventually { driver.waitingForChanges }
        return controller
    }

    @Test func listensAfterConnectAndSlowsPollingToAHeartbeat() async {
        let driver = FakeDriver()
        let c = await listening(driver)
        #expect(driver.changeCalls == 1)
        #expect(!c.changesActive)
        #expect(c.healthyPollDelay() == .seconds(4))

        driver.report(.none)
        await eventually { driver.changeCalls == 2 }
        #expect(c.changesActive)
        #expect(c.healthyPollDelay() == .seconds(10))
        c.isPanelVisible = true
        #expect(c.healthyPollDelay() == .seconds(3))
        #expect(driver.writes.isEmpty)
    }

    @Test func echoOfOwnWriteCostsNoRead() async {
        let driver = FakeDriver()
        let c = await listening(driver)
        c.stepVolume(1)
        await settle(c)
        let reads = driver.reads
        driver.report(DeviceChanges(volume: 21))
        await eventually { driver.changeCalls == 2 }
        #expect(driver.reads == reads)
        #expect(c.snapshot?.volume == 21)
    }

    @Test func changeMadeElsewhereShowsAtOnce() async {
        let driver = FakeDriver()
        let c = await listening(driver)
        driver.volume = 25
        driver.report(DeviceChanges(volume: 25))
        await eventually { c.snapshot?.volume == 25 }
        #expect(c.snapshot?.volume == 25)

        driver.source = "wifi"
        driver.report(DeviceChanges(state: true))
        await eventually { c.snapshot?.sourceId == "wifi" }
        #expect(c.snapshot?.sourceId == "wifi")
        #expect(driver.writes.isEmpty)
    }

    @Test func changesDuringACommandAreLookedAtAfterwards() async {
        let driver = FakeDriver()
        let c = await listening(driver)
        driver.holdNextRead = true
        c.toggleMute()
        await eventually { driver.isHolding }
        driver.source = "wifi"
        driver.report(DeviceChanges(state: true))
        await eventually { driver.changeCalls == 2 }
        let reads = driver.reads
        driver.release()
        await settle(c)
        await eventually { driver.reads > reads + 1 && c.snapshot?.sourceId == "wifi" }
        #expect(c.snapshot?.sourceId == "wifi")
    }

    @Test func failedReportsFallBackToPollingAndRetry() async {
        let driver = FakeDriver()
        let c = await listening(driver)
        driver.report(.none)
        await eventually { driver.changeCalls == 2 }
        let reads = driver.reads
        driver.failChanges()
        await eventually { driver.changeCalls == 3 }
        #expect(!c.changesActive)
        #expect(driver.reads == reads + 1)
        #expect(c.isOnline)
    }

    @Test func speakerGoneStopsListeningUntilItIsBack() async {
        let driver = FakeDriver()
        let c = await listening(driver)
        driver.broken = true
        driver.failChanges()
        // The read right after the failure is the one missed poll that is tolerated.
        await eventually { driver.changeCalls == 2 }
        #expect(c.isOnline)
        await c.refresh()
        #expect(!c.isOnline)
        #expect(!c.changesActive)

        driver.broken = false
        await c.refresh()
        await eventually { driver.changeCalls == 3 }
        #expect(c.isOnline && driver.changeCalls == 3)
    }

    @Test func noListeningWithoutTheCapability() async {
        let driver = FakeDriver()
        _ = await connected(driver)
        for _ in 0..<50 { await Task.yield() }
        #expect(driver.changeCalls == 0)
    }

    @Test func playbackShowsOnlyOnAStreamingInput() async {
        let driver = FakeDriver()
        driver.source = "wifi"
        driver.playing = Self.song()
        let c = await listening(driver, playback: true)
        await eventually { c.showsPlayback }
        #expect(c.playback.title == "Love Me Not")

        driver.source = "usb"
        driver.report(DeviceChanges(state: true))
        await eventually { c.playback == .none }
        #expect(!c.showsPlayback)
        #expect(c.playback == .none)
    }

    @Test func playbackOffReadsNothing() async {
        let driver = FakeDriver()
        driver.source = "wifi"
        driver.playing = Self.song()
        let c = await listening(driver)
        driver.report(DeviceChanges(playback: true))
        await eventually { driver.changeCalls == 2 }
        #expect(driver.playbackReads == 0)
        c.playbackEnabled = true
        await eventually { c.showsPlayback }
        #expect(c.showsPlayback)
        c.playbackEnabled = false
        #expect(!c.showsPlayback)
    }

    @Test func togglePauseShowsAtOnceAndIsConfirmed() async {
        let driver = FakeDriver()
        driver.source = "wifi"
        driver.playing = Self.song()
        let c = await listening(driver, playback: true)
        await eventually { c.showsPlayback }
        driver.playbackLagReads = 2
        c.sendPlayback(.togglePause)
        await settle(c)
        #expect(driver.playbackCommands == [.togglePause])
        #expect(c.playback.status == .paused)
        await eventually { driver.playbackReads >= 4 }
        #expect(c.playback.status == .paused)
        #expect(driver.writes.isEmpty)
    }

    @Test func nextIsConfirmedByTheNewTrack() async {
        let driver = FakeDriver()
        driver.source = "wifi"
        driver.playing = Self.song()
        let c = await listening(driver, playback: true)
        await eventually { c.showsPlayback }
        driver.playbackLagReads = 1
        c.sendPlayback(.next)
        await settle(c)
        await eventually { c.playback.title == "Love Me Not (next)" }
        #expect(c.playback.title == "Love Me Not (next)")
    }

    @Test func playbackIsReadAgainAfterReconnectingDuringAConfirmation() async {
        let driver = FakeDriver()
        driver.source = "wifi"
        driver.playing = Self.song()
        driver.extraCapabilities = [.changes, .playback]
        // The confirmation's reads never come.
        let c = SpeakerController(sleep: { duration in
            if duration == .milliseconds(300) { try await Task.sleep(for: .seconds(60)) }
        })
        c.appLimit = 30
        c.playbackEnabled = true
        await c.connect(driver)
        await eventually { c.showsPlayback }
        c.sendPlayback(.next)
        await settle(c)

        let other = FakeDriver()
        other.source = "wifi"
        var song = Self.song()
        song.title = "After reconnecting"
        other.playing = song
        other.extraCapabilities = [.changes, .playback]
        await c.connect(other)
        await eventually { c.playback.title == "After reconnecting" }
        #expect(c.playback.title == "After reconnecting")
    }

    @Test func rejectedPlaybackCommandKeepsTheConnection() async {
        let driver = FakeDriver()
        driver.source = "wifi"
        driver.playing = Self.song()
        let c = await listening(driver, playback: true)
        driver.rejectPlayback = true
        c.sendPlayback(.next)
        await settle(c)
        #expect(c.notice != nil)
        #expect(c.isOnline)
    }

    @Test func positionCountsOnWhilePlaying() async {
        let driver = FakeDriver()
        driver.source = "wifi"
        var song = Self.song()
        song.positionMs = 1000
        song.durationMs = 5000
        driver.playing = song
        let clock = Clock(now: 100)
        let c = await listening(driver, playback: false)
        c.uptime = { clock.now }
        c.playbackEnabled = true
        await eventually { c.showsPlayback }
        clock.now += 1.5
        #expect(c.playbackPositionMs() == 2500)
        clock.now += 10
        #expect(c.playbackPositionMs() == 5000)

        song.status = .paused
        song.positionMs = 3000
        driver.playing = song
        await c.refreshPlayback()
        clock.now += 5
        #expect(c.playbackPositionMs() == 3000)
    }

    @Test func disconnectClearsPlayback() async {
        let driver = FakeDriver()
        driver.source = "wifi"
        driver.playing = Self.song()
        let c = await listening(driver, playback: true)
        await eventually { c.showsPlayback }
        c.disconnect()
        #expect(c.playback == .none)
        #expect(!c.showsPlayback)
    }
}

final class Clock: @unchecked Sendable {
    var now: Double
    init(now: Double) { self.now = now }
}
