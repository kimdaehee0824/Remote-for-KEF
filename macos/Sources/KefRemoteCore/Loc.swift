import Foundation

public enum UILanguage: String, CaseIterable, Sendable {
    case korean = "ko"
    case english = "en"
    case japanese = "ja"

    /// "ko", "EN", "ja-JP" and the like; nil for other languages.
    public init?(code: String) {
        let base = code.trimmingCharacters(in: .whitespaces).lowercased()
            .split(whereSeparator: { $0 == "-" || $0 == "_" }).first
        self.init(rawValue: base.map(String.init) ?? "")
    }

    /// The first of Korean, English or Japanese in a preferred-languages list, as macOS
    /// picks an app's localization; English when none of them is there.
    public static func preferred(_ languages: [String]) -> UILanguage {
        languages.lazy.compactMap(UILanguage.init(code:)).first ?? .english
    }

    /// The value of `--lang` among launch arguments, if it names one of the three.
    public static func launchOption(_ arguments: [String]) -> UILanguage? {
        guard let index = arguments.firstIndex(of: "--lang"), index + 1 < arguments.count else { return nil }
        return UILanguage(code: arguments[index + 1])
    }
}

/// UI text in Korean, English and Japanese. The language follows the system's preferred
/// languages (System Settings can also set one for this app) unless `--lang` picks one at launch.
public enum Loc {
    /// Set once at launch, before any text is shown.
    nonisolated(unsafe) public static var language = UILanguage.preferred(Locale.preferredLanguages)
}

/// The text in the UI language.
public func loc(_ ko: String, _ en: String, _ ja: String) -> String {
    switch Loc.language {
    case .korean: ko
    case .english: en
    case .japanese: ja
    }
}
