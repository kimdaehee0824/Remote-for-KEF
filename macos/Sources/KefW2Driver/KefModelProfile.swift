import Foundation
import KefRemoteCore

/// W2 platform models, mirroring windows/src/KefRemote.Drivers/Kef/KefModels.cs.
/// Source lists follow EvotecIT/homeassistant-kef (const.py, MODERN_MODEL_SOURCE_MAP).
/// Only LSX II LT was verified on real hardware for this project (usb and wifi switching).
struct KefModelProfile: Sendable, Equatable {
    let id: String
    let displayName: String
    let sourceIds: [String]
    let verified: Bool

    var sources: [SourceOption] { sourceIds.map { SourceOption(id: $0, name: Self.label(for: $0)) } }

    static let defaultSources = ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog", "usb"]

    static let known: [KefModelProfile] = [
        KefModelProfile(id: "LSXIILT", displayName: "LSX II LT", sourceIds: ["usb", "wifi", "bluetooth", "tv", "optical"], verified: true),
        KefModelProfile(id: "LSXII", displayName: "LSX II", sourceIds: ["wifi", "bluetooth", "tv", "optical", "analog", "usb"], verified: false),
        KefModelProfile(id: "LS50W2", displayName: "LS50 Wireless II", sourceIds: ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog"], verified: false),
        KefModelProfile(id: "LS60", displayName: "LS60 Wireless", sourceIds: ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog"], verified: false),
        KefModelProfile(id: "XIO", displayName: "XIO", sourceIds: ["wifi", "bluetooth", "tv", "optical"], verified: false),
    ]

    static let aliases = ["LSX2LT": "LSXIILT", "LSX2": "LSXII", "LS50WII": "LS50W2", "LS60W": "LS60"]

    /// Some firmware reports an internal code as modelName (LSX II LT reports
    /// "SP4077"), so the release text prefix ("LSXIILT_V2186") is used as well.
    /// Unknown W2 models get the default source list and stay unverified.
    static func resolve(modelName: String, releaseText: String) -> KefModelProfile {
        let prefix = releasePrefix(releaseText)
        for candidate in [modelName, prefix] {
            var id = normalize(candidate)
            id = aliases[id] ?? id
            if let profile = known.first(where: { $0.id == id }) { return profile }
        }
        let label = prefix.trimmingCharacters(in: .whitespaces).isEmpty ? modelName : prefix
        return KefModelProfile(id: normalize(label), displayName: "KEF \(label)", sourceIds: defaultSources, verified: false)
    }

    static func label(for id: String) -> String {
        switch id {
        case "usb": "USB-C / PC"
        case "wifi": "Wi-Fi"
        case "bluetooth": "Bluetooth"
        case "tv": "TV (HDMI eARC)"
        case "optical": "Optical"
        case "coaxial": "Coaxial"
        case "analog": "Analog"
        default: id
        }
    }

    static func releasePrefix(_ text: String) -> String {
        guard let range = text.range(of: "_V", options: .caseInsensitive), range.lowerBound > text.startIndex else { return "" }
        return String(text[..<range.lowerBound])
    }

    static func normalize(_ text: String) -> String {
        text.replacingOccurrences(of: " ", with: "").replacingOccurrences(of: "-", with: "").uppercased()
    }
}
