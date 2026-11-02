import Foundation
import Testing
@testable import KefRemoteCore

/// The list of registered speakers, and the move from the single speaker of 0.4.
struct SpeakerListTests {
    @Test func theSingleSpeakerOf04BecomesTheFirstEntry() {
        let legacy = LegacySpeaker(host: " 192.168.0.20 ", appLimit: 40, approachFromAbove: true,
                                   finish: "lsx-ii-lt/sage-green", pickedModel: "LSX II LT", lastInput: "usb")
        let list = SpeakerList.migrated(from: legacy)
        #expect(list.count == 1)
        let entry = list[0]
        #expect(entry.host == "192.168.0.20")
        #expect(entry.appLimit == 40 && entry.approachFromAbove)
        #expect(entry.finish == "lsx-ii-lt/sage-green" && entry.pickedModel == "LSX II LT" && entry.lastInput == "usb")
        // Its password stays where 0.4 put it.
        #expect(entry.keychainAccount == SpeakerEntry.legacyKeychainAccount)
    }

    @Test func nothingSetUpGivesAnEmptyList() {
        #expect(SpeakerList.migrated(from: nil).isEmpty)
        #expect(SpeakerList.migrated(from: LegacySpeaker(host: "  ")).isEmpty)
        #expect(SpeakerList.selected([], id: nil) == nil)
    }

    @Test func limitsAreClamped() {
        #expect(SpeakerList.migrated(from: LegacySpeaker(host: "192.168.0.20", appLimit: 0))[0].appLimit == 1)
        #expect(SpeakerEntry(host: "192.168.0.20", appLimit: 400).appLimit == 100)
        #expect(SpeakerEntry(host: "192.168.0.20").appLimit == VolumePolicy.defaultAppLimit)
    }

    @Test func newSpeakersGetTheirOwnKeychainItem() {
        let a = SpeakerEntry(host: "192.168.0.20"), b = SpeakerEntry(host: "192.168.0.21")
        #expect(a.id != b.id)
        #expect(a.keychainAccount != b.keychainAccount)
        #expect(a.keychainAccount != SpeakerEntry.legacyKeychainAccount)
    }

    @Test func selectionFallsBackToTheFirst() {
        let list = [SpeakerEntry(host: "192.168.0.20"), SpeakerEntry(host: "192.168.0.21")]
        #expect(SpeakerList.selected(list, id: list[1].id) == list[1])
        #expect(SpeakerList.selected(list, id: "gone") == list[0])
        #expect(SpeakerList.selected(list, id: nil) == list[0])
    }

    @Test func nextWrapsAround() {
        let list = [SpeakerEntry(host: "192.168.0.20"), SpeakerEntry(host: "192.168.0.21"), SpeakerEntry(host: "192.168.0.22")]
        #expect(SpeakerList.next(list, after: list[0].id) == list[1])
        #expect(SpeakerList.next(list, after: list[2].id) == list[0])
        #expect(SpeakerList.next(Array(list.prefix(1)), after: list[0].id) == nil)
    }

    @Test func removingTheSelectedOneHandsOverToTheOneBefore() {
        let list = [SpeakerEntry(host: "192.168.0.20"), SpeakerEntry(host: "192.168.0.21"), SpeakerEntry(host: "192.168.0.22")]
        var (rest, selected) = SpeakerList.removing(list[1].id, from: list, selected: list[1].id)
        #expect(rest.map(\.id) == [list[0].id, list[2].id] && selected == list[0].id)
        (rest, selected) = SpeakerList.removing(list[0].id, from: rest, selected: selected)
        #expect(selected == list[2].id)
        (rest, selected) = SpeakerList.removing(list[2].id, from: rest, selected: selected)
        #expect(rest.isEmpty && selected == nil)
    }

    @Test func removingAnotherOneKeepsTheSelection() {
        let list = [SpeakerEntry(host: "192.168.0.20"), SpeakerEntry(host: "192.168.0.21")]
        let (rest, selected) = SpeakerList.removing(list[1].id, from: list, selected: list[0].id)
        #expect(rest == [list[0]] && selected == list[0].id)
        #expect(SpeakerList.removing("gone", from: list, selected: list[0].id).list == list)
    }

    @Test func theSameAddressIsFoundWhateverTheSpacing() {
        let list = [SpeakerEntry(host: "192.168.0.20")]
        #expect(SpeakerList.withHost(" 192.168.0.20 ", in: list) == list[0])
        #expect(SpeakerList.withHost("192.168.0.20", in: list, except: list[0].id) == nil)
        #expect(SpeakerList.withHost("192.168.0.21", in: list) == nil)
    }

    @Test func savedListsReadBackAndToleratePartialEntries() throws {
        var entry = SpeakerEntry(host: "192.168.0.20", name: "Desk", model: "LSX II LT", appLimit: 25)
        entry.lastInput = "wifi"
        let data = try JSONEncoder().encode([entry])
        #expect(try JSONDecoder().decode([SpeakerEntry].self, from: data) == [entry])
        let partial = try JSONDecoder().decode([SpeakerEntry].self, from: Data(#"[{"id":"abc","host":"192.168.0.21"}]"#.utf8))
        #expect(partial[0].id == "abc" && partial[0].appLimit == VolumePolicy.defaultAppLimit && !partial[0].approachFromAbove)
        #expect(partial[0].keychainAccount == "speaker-webpage-password-abc")
    }

    @Test func displayNamePrefersTheReportedName() {
        var entry = SpeakerEntry(host: "192.168.0.20")
        #expect(SpeakerList.displayName(entry) == "192.168.0.20")
        entry.name = "Living room"
        #expect(SpeakerList.displayName(entry) == "Living room")
    }
}
