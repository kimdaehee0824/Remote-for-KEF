import Testing
@testable import KefW2Driver

struct KefFinishTests {
    @Test func everyKnownModelHasFinishes() {
        for model in KefModels.names {
            #expect(!KefFinish.finishes(for: model).isEmpty)
            #expect(KefFinish.finishes(for: model).allSatisfy { $0.model == model })
        }
        #expect(KefFinish.finishes(for: "LSX II LT").map(\.name) == ["Sage Green", "Stone White", "Graphite Grey"])
        #expect(KefFinish.finishes(for: "LS LUXE").map(\.name) == ["Dusk Titanium", "Eclipse Black", "Mineral White"])
    }

    @Test func idsAreUnique() {
        #expect(Set(KefFinish.all.map(\.id)).count == KefFinish.all.count)
    }

    @Test func unknownModelHasNoFinishes() {
        #expect(KefFinish.finishes(for: "KEF LS30").isEmpty)
        #expect(KefFinish.finishes(for: nil).isEmpty)
    }

    @Test func edgeIsLighterOnDarkCabinetsAndDarkerOnLightOnes() {
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: nil)?.edge == 0x7E8E84)
        #expect(KefFinish.find("lsx-ii-lt/stone-white", model: nil)?.edge == 0xAFA9A2)
    }

    @Test func appDefaultIsRoyalBlueWithItsOutline() {
        #expect(KefFinish.appDefault.id == "ls60-wireless/royal-blue")
        #expect(KefFinish.appDefault.cabinet == 0x3A527B)
        #expect(KefFinish.appDefault.edge == 0x7F8FA9)
    }

    @Test func savedFinishOfAnotherModelIsIgnored() {
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: "LSX II LT")?.name == "Sage Green")
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: "LS50 Wireless II") == nil)
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: nil)?.name == "Sage Green")
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: "KEF LS30") == nil)
        #expect(KefFinish.find("lsx-ii-lt/purple", model: "LSX II LT") == nil)
        #expect(KefFinish.find(nil, model: "LSX II LT") == nil)
    }

    @Test func shadesAreMixedFromTheCabinet() throws {
        let sage = try #require(KefFinish.find("lsx-ii-lt/sage-green", model: nil))
        #expect(sage.color(.cabinet) == sage.cabinet)
        #expect(sage.color(.edge) == sage.edge)
        #expect(sage.color(.tweeter) == sage.tweeter)
        #expect(sage.color(.fabric) == 0x516659)
        #expect(sage.color(.seam) == 0x1F2D24)
        #expect(sage.color(.plinth) == 0x5C7063)
        #expect(sage.color(.top) == 0x596D60)
    }
}
