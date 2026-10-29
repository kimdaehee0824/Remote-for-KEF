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
        KefFinish(id: "ls50-wireless-ii/carbon-black", model: "LS50 Wireless II", name: "Carbon Black", cabinet: 0x4C5051, trim: 0x464645, cone: 0xA97051, tweeter: 0x81472B),
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

        // XIO soundbar. Its drivers sit behind the grille, so the icon uses darker shades of it.
        KefFinish(id: "xio/silver-grey", model: "XIO", name: "Silver Grey", cabinet: 0x9F9F97, trim: 0x72726C, cone: 0x888881, tweeter: 0x72726C),
        KefFinish(id: "xio/slate-black", model: "XIO", name: "Slate Black", cabinet: 0x34312C, trim: 0x25231F, cone: 0x2C2A25, tweeter: 0x25231F),
    ]

    /// The finishes of a model by its display name, or every finish when the model is not in the table.
    public static func finishes(for model: String?) -> [KefFinish] {
        let own = all.filter { $0.model == model }
        return own.isEmpty ? all : own
    }

    /// The saved finish, unless it belongs to another known model (the speaker was replaced).
    /// Before the model is known the saved finish is used as is.
    public static func find(_ id: String?, model: String?) -> KefFinish? {
        guard let id, !id.isEmpty, let finish = all.first(where: { $0.id == id }) else { return nil }
        guard let model, !model.isEmpty else { return finish }
        return finishes(for: model).contains(finish) ? finish : nil
    }
}
