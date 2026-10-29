import Foundation
import KefRemoteCore

/// W2 platform models, mirroring windows/src/KefRemote.Drivers/Kef/KefModels.cs.
/// Source lists follow EvotecIT/homeassistant-kef (const.py, MODERN_MODEL_SOURCE_MAP),
/// and KEF's specifications for LS LUXE, which came out after it.
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
        KefModelProfile(id: "LSLUXE", displayName: "LS LUXE", sourceIds: ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog"], verified: false),
    ]

    /// The speakers report the first six characters of the KEF part number as modelName ("SP4077J2CA" is an
    /// LSX II LT in Sage Green). SP4077, SP4041 and SP4083 were seen on real speakers; the other three follow
    /// the part numbers KEF sells them under.
    static let aliases = [
        "LSX2LT": "LSXIILT", "LSX2": "LSXII", "LS50WII": "LS50W2", "LS60W": "LS60",
        "SP4077": "LSXIILT", "SP4041": "LSXII", "SP4025": "LS50W2", "SP4017": "LS60", "SP4083": "XIO", "SP4042": "LSLUXE",
    ]

    /// The release text prefix ("LSXIILT_V2186") is used as well, for part numbers not in the table.
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

/// The models in the table by display name, for the speaker drawing and the finish list.
/// Mirrors KefModels.Names and KefModels.Shown in windows/src/KefRemote.Drivers/Kef/KefModels.cs.
public enum KefModels {
    /// Display names of the models in the table, to pick from when a speaker reports one the app does not know.
    public static let names = KefModelProfile.known.map(\.displayName)

    /// The model the speaker is drawn as and whose finishes are listed: the reported one when it is in the table,
    /// otherwise the one picked in settings, otherwise the reported one as is (nil before the speaker is connected).
    /// The picked model is not used for anything else; the inputs stay those of the reported model.
    public static func shown(reported: String?, picked: String?) -> String? {
        if let reported, names.contains(reported) { return reported }
        if let picked, names.contains(picked) { return picked }
        return reported
    }
}
