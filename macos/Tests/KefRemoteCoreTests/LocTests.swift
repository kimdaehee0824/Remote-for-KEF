import Testing
@testable import KefRemoteCore

// Loc.language is shared by every test, so these cover only the pure parts.
struct LocTests {
    @Test func firstSupportedPreferredLanguageWins() {
        #expect(UILanguage.preferred(["ko-KR", "en-US"]) == .korean)
        #expect(UILanguage.preferred(["ja-JP"]) == .japanese)
        #expect(UILanguage.preferred(["en-KR", "ko-KR"]) == .english)
        #expect(UILanguage.preferred(["fr-FR", "ja-JP"]) == .japanese)
        #expect(UILanguage.preferred(["de-DE", "zh-Hans"]) == .english)
        #expect(UILanguage.preferred([]) == .english)
    }

    @Test func langOptionAcceptsKoEnJa() {
        #expect(UILanguage.launchOption(["KEFRemote", "--demo", "--lang", "ja"]) == .japanese)
        #expect(UILanguage.launchOption(["KEFRemote", "--lang", "EN"]) == .english)
        #expect(UILanguage.launchOption(["KEFRemote", "--lang", "fr"]) == nil)
        #expect(UILanguage.launchOption(["KEFRemote", "--lang"]) == nil)
        #expect(UILanguage.launchOption(["KEFRemote"]) == nil)
    }
}
