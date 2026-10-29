import Foundation

/// One KEF finish as the colors of the speaker icon, each 0xRRGGBB: cabinet, the trim ring
/// around the Uni-Q driver, its cone and the tweeter in the middle.
/// Mirrors windows/src/KefRemote.Drivers/Kef/KefFinishes.cs.
public struct KefFinish: Sendable, Equatable, Identifiable {
    public let id: String
    public let model: String
    public let name: String
    public let cabinet: UInt32
    public let trim: UInt32
    public let cone: UInt32
    public let tweeter: UInt32

    /// Outline of the cabinet, lighter than a dark cabinet and darker than a light one, so the
    /// speaker keeps its shape on a background of a similar color (a dark speaker in dark mode).
    public var edge: UInt32 {
        Self.mix(cabinet, dark ? 0xFFFFFF : 0, dark ? 0.35 : 0.18)
    }

    /// The color of a part of the speaker drawing (`KefShape`). The shades are mixed from the cabinet.
    public func color(_ paint: KefPaint) -> UInt32 {
        switch paint {
        case .cabinet: cabinet
        case .edge: edge
        case .trim: trim
        case .cone: cone
        case .tweeter: tweeter
        // LSX II fabric wrap, set off from the cabinet-colored front a little.
        case .fabric: Self.mix(cabinet, dark ? 0xFFFFFF : 0, dark ? 0.12 : 0.1)
        // The gap between the LSX II fabric and its front.
        case .seam: Self.mix(cabinet, 0, dark ? 0.45 : 0.35)
        // LS60 plinth, halfway to the outline.
        case .plinth: Self.mix(cabinet, edge, 0.5)
        // XIO top face, lit from above.
        case .top: Self.mix(cabinet, 0xFFFFFF, dark ? 0.16 : 0.3)
        }
    }

    private var dark: Bool { Self.luminance(cabinet) < 0.5 }

    private static func component(_ rgb: UInt32, _ shift: UInt32) -> Double {
        Double((rgb >> shift) & 0xFF)
    }

    private static func luminance(_ rgb: UInt32) -> Double {
        let red = 0.2126 * component(rgb, 16)
        let green = 0.7152 * component(rgb, 8)
        let blue = 0.0722 * component(rgb, 0)
        return (red + green + blue) / 255
    }

    private static func mix(_ a: UInt32, _ b: UInt32, _ t: Double) -> UInt32 {
        func channel(_ shift: UInt32) -> UInt32 {
            let value = component(a, shift) * (1 - t) + component(b, shift) * t
            return UInt32(value.rounded()) << shift
        }
        return channel(16) | channel(8) | channel(0)
    }
}

/// Finishes per model, so the speaker icon can match the speaker. The speakers report only a
/// model code ("SP4077"), not the finish, so the user picks it. KEF publishes no color codes;
/// the values were sampled from KEF product photos and are close, not exact.
extension KefFinish {
    public static let all: [KefFinish] = [
        // LSX II LT
        KefFinish(id: "lsx-ii-lt/sage-green", model: "LSX II LT", name: "Sage Green", cabinet: 0x395142, trim: 0x323232, cone: 0x2E3E3C, tweeter: 0x49514F),
        KefFinish(id: "lsx-ii-lt/stone-white", model: "LSX II LT", name: "Stone White", cabinet: 0xD6CEC6, trim: 0x414040, cone: 0xBAB2AB, tweeter: 0x696561),
        KefFinish(id: "lsx-ii-lt/graphite-grey", model: "LSX II LT", name: "Graphite Grey", cabinet: 0x44413C, trim: 0x323232, cone: 0x3C3B36, tweeter: 0x474643),

        // LSX II. Mineral White, Cobalt Blue, Olive Green and Lava Red are no longer sold.
        KefFinish(id: "lsx-ii/amber-haze", model: "LSX II", name: "Amber Haze", cabinet: 0xD39645, trim: 0x3F3C39, cone: 0xB5987B, tweeter: 0x836245),
        KefFinish(id: "lsx-ii/cotton-white", model: "LSX II", name: "Cotton White", cabinet: 0xD5CFCD, trim: 0x413F3D, cone: 0xA99D98, tweeter: 0x9B8067),
        KefFinish(id: "lsx-ii/carbon-black", model: "LSX II", name: "Carbon Black", cabinet: 0x242529, trim: 0x27292C, cone: 0x69645D, tweeter: 0x907458),
        KefFinish(id: "lsx-ii/soundwave-by-terence-conran", model: "LSX II", name: "Soundwave by Terence Conran", cabinet: 0x877468, trim: 0x26292C, cone: 0x927F73, tweeter: 0x2B3578),
        KefFinish(id: "lsx-ii/mineral-white", model: "LSX II", name: "Mineral White", cabinet: 0xD9D2D0, trim: 0x282A2D, cone: 0xA99F99, tweeter: 0x9C8065),
        KefFinish(id: "lsx-ii/cobalt-blue", model: "LSX II", name: "Cobalt Blue", cabinet: 0x27335A, trim: 0x323438, cone: 0xB6987A, tweeter: 0x856546),
        KefFinish(id: "lsx-ii/olive-green", model: "LSX II", name: "Olive Green", cabinet: 0x505F3B, trim: 0x33312F, cone: 0xD2B7A2, tweeter: 0x997E67),
        KefFinish(id: "lsx-ii/lava-red", model: "LSX II", name: "Lava Red", cabinet: 0x831412, trim: 0x323538, cone: 0x962B2B, tweeter: 0x721F1F),

        // LS50 Wireless II. Titanium Grey and Crimson Red are no longer sold.
        KefFinish(id: "ls50-wireless-ii/carbon-black", model: "LS50 Wireless II", name: "Carbon Black", cabinet: 0x4C5051, trim: 0x2E2E2E, cone: 0xA97051, tweeter: 0x81472B),
        KefFinish(id: "ls50-wireless-ii/mineral-white", model: "LS50 Wireless II", name: "Mineral White", cabinet: 0xEFEDE5, trim: 0x4D4B49, cone: 0xA97051, tweeter: 0x81472B),
        KefFinish(id: "ls50-wireless-ii/sand-shell", model: "LS50 Wireless II", name: "Sand Shell", cabinet: 0xD9CFC5, trim: 0x434040, cone: 0xD0B29B, tweeter: 0xA98564),
        KefFinish(id: "ls50-wireless-ii/moss-green", model: "LS50 Wireless II", name: "Moss Green", cabinet: 0x5D6B5C, trim: 0x423F3F, cone: 0xD0B29A, tweeter: 0xA98664),
        KefFinish(id: "ls50-wireless-ii/titanium-grey", model: "LS50 Wireless II", name: "Titanium Grey", cabinet: 0x9D9895, trim: 0x4C4646, cone: 0x963F39, tweeter: 0x553430),
        KefFinish(id: "ls50-wireless-ii/crimson-red-special-edition", model: "LS50 Wireless II", name: "Crimson Red Special Edition", cabinet: 0x9F2C2D, trim: 0x4B3F40, cone: 0x6C6561, tweeter: 0x4C3E3C),

        // LS60 Wireless
        KefFinish(id: "ls60-wireless/mineral-white", model: "LS60 Wireless", name: "Mineral White", cabinet: 0xE7E3E2, trim: 0x25282B, cone: 0xC3BDB9, tweeter: 0xA7875C),
        KefFinish(id: "ls60-wireless/carbon-black", model: "LS60 Wireless", name: "Carbon Black", cabinet: 0x3D3D3D, trim: 0x3A3A3A, cone: 0x6E6D6C, tweeter: 0x4A4540),
        KefFinish(id: "ls60-wireless/titanium-grey", model: "LS60 Wireless", name: "Titanium Grey", cabinet: 0x6F6E6D, trim: 0x26292C, cone: 0xC4A385, tweeter: 0xA98758),
        KefFinish(id: "ls60-wireless/royal-blue", model: "LS60 Wireless", name: "Royal Blue", cabinet: 0x3A527B, trim: 0x25282D, cone: 0xD3B396, tweeter: 0xAA8958),

        // LS LUXE
        KefFinish(id: "ls-luxe/dusk-titanium", model: "LS LUXE", name: "Dusk Titanium", cabinet: 0xC4B9AB, trim: 0x3B3A38, cone: 0xBFAE97, tweeter: 0x9C7A4A),
        KefFinish(id: "ls-luxe/eclipse-black", model: "LS LUXE", name: "Eclipse Black", cabinet: 0x2F2F2F, trim: 0x1F1F1E, cone: 0x666262, tweeter: 0x8A6E45),
        KefFinish(id: "ls-luxe/mineral-white", model: "LS LUXE", name: "Mineral White", cabinet: 0xDADADA, trim: 0x393836, cone: 0xB8B8B6, tweeter: 0xA08455),

        // XIO soundbar. Its drivers sit behind the grille, so the icon uses darker shades of it.
        KefFinish(id: "xio/silver-grey", model: "XIO", name: "Silver Grey", cabinet: 0x9F9F97, trim: 0x72726C, cone: 0x888881, tweeter: 0x72726C),
        KefFinish(id: "xio/slate-black", model: "XIO", name: "Slate Black", cabinet: 0x34312C, trim: 0x25231F, cone: 0x2C2A25, tweeter: 0x25231F),
    ]

    /// The app's own colors when no finish is picked, also those of the Windows app icon:
    /// LS60 Wireless Royal Blue, which sits well next to the default blue accent of macOS and Windows.
    public static let appDefault = all.first { $0.id == "ls60-wireless/royal-blue" }!

    /// The finishes of a model by its display name, none for a model that is not in the table.
    public static func finishes(for model: String?) -> [KefFinish] {
        all.filter { $0.model == model }
    }

    /// The saved finish, unless it belongs to another model (the speaker was replaced, or the app does not know the model).
    /// Before the speaker is connected the saved finish is used as is.
    public static func find(_ id: String?, model: String?) -> KefFinish? {
        guard let id, !id.isEmpty, let finish = all.first(where: { $0.id == id }) else { return nil }
        guard let model, !model.isEmpty else { return finish }
        return finish.model == model ? finish : nil
    }
}
