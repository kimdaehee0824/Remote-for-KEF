import Testing
@testable import KefW2Driver

struct KefFinishTests {
    @Test func everyKnownModelHasFinishes() {
        for model in ["LSX II LT", "LSX II", "LS50 Wireless II", "LS60 Wireless", "XIO"] {
            #expect(KefFinish.finishes(for: model).allSatisfy { $0.model == model })
        }
        #expect(KefFinish.finishes(for: "LSX II LT").map(\.name) == ["Sage Green", "Stone White", "Graphite Grey"])
    }

    @Test func idsAreUnique() {
        #expect(Set(KefFinish.all.map(\.id)).count == KefFinish.all.count)
    }

    @Test func unknownModelOffersEveryFinish() {
        #expect(KefFinish.finishes(for: "KEF LS30") == KefFinish.all)
    }

    @Test func savedFinishOfAnotherModelIsIgnored() {
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: "LSX II LT")?.name == "Sage Green")
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: "LS50 Wireless II") == nil)
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: nil)?.name == "Sage Green")
        #expect(KefFinish.find("lsx-ii-lt/sage-green", model: "KEF LS30")?.name == "Sage Green")
        #expect(KefFinish.find("lsx-ii-lt/purple", model: "LSX II LT") == nil)
        #expect(KefFinish.find(nil, model: "LSX II LT") == nil)
    }
}
