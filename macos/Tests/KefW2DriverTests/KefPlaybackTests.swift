import Foundation
import Testing
@testable import KefRemoteCore
@testable import KefW2Driver

/// Now playing, player control and the event queue of the KEF W2 driver.
struct KefPlaybackTests {
    private func driver(_ device: FakeKefDevice, password: String = "") async throws -> KefW2Driver {
        let driver = try KefW2Driver(host: "192.168.0.14", password: password, transport: device)
        _ = try await driver.connect()
        return driver
    }

    @Test func controlNeverSendsBarePlay() {
        for command in PlaybackCommand.allCases {
            #expect(!KefPlayback.controlValue(command).contains(#""play""#))
        }
        #expect(KefPlayback.controlValue(.togglePause) == #"{"control":"pause"}"#)
        #expect(KefPlayback.controlValue(.next) == #"{"control":"next"}"#)
        #expect(KefPlayback.controlValue(.previous) == #"{"control":"previous"}"#)
    }

    @Test func queueRequests() throws {
        #expect(KefPlayback.modifyQueueBody(["player:volume"])
            == #"{"subscribe":[{"path":"player:volume","type":"itemWithValue"}],"unsubscribe":[]}"#)
        let client = try KefW2Client(host: "192.168.0.14", password: "", transport: FakeKefDevice())
        #expect(client.pollQueueURL("{caa6b0ae}", hold: 10) == "http://192.168.0.14/api/event/pollQueue?queueId=%7Bcaa6b0ae%7D&timeout=10")
    }

    @Test func emptyPlayerIsNothing() throws {
        for text in ["[]", "[{}]", "null"] {
            #expect(try !KefPlayback.parse(KefJSON.decode(Data(text.utf8)), playTime: nil).hasTrack)
        }
    }

    @Test func readsWhatIsPlaying() async throws {
        let device = FakeKefDevice()
        await device.set("settings:/kef/play/physicalSource", #"[{"type":"kefPhysicalSource","kefPhysicalSource":"wifi"}]"#)
        await device.set(KefPlayback.dataPath, #"[{"state":"playing","trackRoles":{"title":"Love Me Not","icon":"https://i.scdn.co/image/x","mediaData":{"metaData":{"artist":"Ravyn Lenae","serviceID":"spotify"}}},"status":{"duration":213466},"controls":{"pause":true,"next_":true,"previous":true}}]"#)
        await device.set(KefPlayback.timePath, #"[{"i64_":49876,"type":"i64_"}]"#)
        let driver = try await driver(device)
        #expect(driver.sourceHasPlayback("wifi"))
        #expect(!driver.sourceHasPlayback("usb"))
        let info = try await driver.readPlayback()
        #expect(info.title == "Love Me Not" && info.artist == "Ravyn Lenae" && info.service == "Spotify")
        #expect(info.positionMs == 49876 && info.durationMs == 213466 && info.canNext)
        #expect(await device.posts.isEmpty)
    }

    @Test func playbackWithoutPositionStillReads() async throws {
        let device = FakeKefDevice()
        await device.set(KefPlayback.dataPath, #"[{"state":"paused","trackRoles":{"title":"Song"}}]"#)
        let info = try await driver(device).readPlayback()
        #expect(info.status == .paused && info.positionMs == -1)
    }

    @Test func playerControlIsSignedWithTheActivateRole() async throws {
        let device = FakeKefDevice(password: "secret")
        let driver = try await driver(device, password: "secret")
        try await driver.sendPlayback(.togglePause)
        try await driver.sendPlayback(.next)
        let posts = await device.posts
        #expect(posts.map(\.path) == [KefPlayback.controlPath, KefPlayback.controlPath])
        #expect(posts.map(\.plain) == [#"{"control":"pause"}"#, #"{"control":"next"}"#])
        #expect(await device.roles == ["activate", "activate"])
        #expect(await device.rejectedSignatures == 0)
    }

    @Test func eventQueueSubscribesOnceUnsignedAndResubscribesAfterAFailure() async throws {
        let device = FakeKefDevice(password: "secret")
        let driver = try await driver(device, password: "secret")
        await device.queue(#"[{"path":"player:volume","itemType":"update","itemValue":{"type":"i32_","i32_":33}}]"#)

        #expect(try await driver.waitForChanges() == DeviceChanges(volume: 33))
        #expect(try await driver.waitForChanges() == .none)
        #expect(await device.queuesCreated == 1)
        let subscriptions = await device.subscriptions
        #expect(subscriptions.contains("settings:/kef/play/physicalSource"))
        #expect(subscriptions.contains(KefPlayback.dataPath))
        #expect(!subscriptions.contains(KefPlayback.timePath))
        #expect(await device.signedEventRequests == 0)
        // Each poll goes out as a held request, not with the 2.5 s timeout.
        #expect(await device.heldRequests == 2)

        await device.breakQueue(true)
        await #expect(throws: KefError.self) { try await driver.waitForChanges() }
        await device.breakQueue(false)
        #expect(try await driver.waitForChanges() == .none)
        #expect(await device.queuesCreated == 2)
    }
}
