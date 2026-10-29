namespace KefRemote.Drivers.Kef;

/// <summary>
/// One KEF finish as the colours of the speaker icon, each 0xRRGGBB: cabinet, the trim ring
/// around the Uni-Q driver, its cone and the tweeter in the middle.
/// </summary>
public sealed record KefFinish(string Id, string Model, string Name, uint Cabinet, uint Trim, uint Cone, uint Tweeter)
{
    /// <summary>
    /// Outline of the cabinet, lighter than a dark cabinet and darker than a light one, so the
    /// speaker keeps its shape on a background of a similar color (a dark speaker in dark mode).
    /// </summary>
    public uint Edge => Mix(Cabinet, Luminance(Cabinet) < 0.5 ? 0xFFFFFFu : 0, Luminance(Cabinet) < 0.5 ? 0.35 : 0.18);

    static double Luminance(uint rgb) => (0.2126 * (rgb >> 16 & 0xFF) + 0.7152 * (rgb >> 8 & 0xFF) + 0.0722 * (rgb & 0xFF)) / 255;

    static uint Mix(uint a, uint b, double t)
    {
        uint Channel(int shift) => (uint)Math.Round((a >> shift & 0xFF) * (1 - t) + (b >> shift & 0xFF) * t) << shift;
        return Channel(16) | Channel(8) | Channel(0);
    }
}

/// <summary>
/// Finishes per model, so the speaker icon can match the speaker. The speakers report only a
/// model code ("SP4077"), not the finish, so the user picks it. KEF publishes no colour codes;
/// the values were sampled from KEF product photos and are close, not exact.
/// </summary>
public static class KefFinishes
{
    public static readonly IReadOnlyList<KefFinish> All =
    [
        // LSX II LT
        new("lsx-ii-lt/sage-green", "LSX II LT", "Sage Green", 0x395142, 0x323232, 0x2E3E3C, 0x49514F),
        new("lsx-ii-lt/stone-white", "LSX II LT", "Stone White", 0xD6CEC6, 0x414040, 0xBAB2AB, 0x696561),
        new("lsx-ii-lt/graphite-grey", "LSX II LT", "Graphite Grey", 0x44413C, 0x323232, 0x3C3B36, 0x474643),

        // LSX II. Mineral White, Cobalt Blue, Olive Green and Lava Red are no longer sold.
        new("lsx-ii/amber-haze", "LSX II", "Amber Haze", 0xD39645, 0x3F3C39, 0xB5987B, 0x836245),
        new("lsx-ii/cotton-white", "LSX II", "Cotton White", 0xD5CFCD, 0x413F3D, 0xA99D98, 0x9B8067),
        new("lsx-ii/carbon-black", "LSX II", "Carbon Black", 0x242529, 0x27292C, 0x69645D, 0x907458),
        new("lsx-ii/soundwave-by-terence-conran", "LSX II", "Soundwave by Terence Conran", 0x877468, 0x26292C, 0x927F73, 0x2B3578),
        new("lsx-ii/mineral-white", "LSX II", "Mineral White", 0xD9D2D0, 0x282A2D, 0xA99F99, 0x9C8065),
        new("lsx-ii/cobalt-blue", "LSX II", "Cobalt Blue", 0x27335A, 0x323438, 0xB6987A, 0x856546),
        new("lsx-ii/olive-green", "LSX II", "Olive Green", 0x505F3B, 0x33312F, 0xD2B7A2, 0x997E67),
        new("lsx-ii/lava-red", "LSX II", "Lava Red", 0x831412, 0x323538, 0x962B2B, 0x721F1F),

        // LS50 Wireless II. Titanium Grey and Crimson Red are no longer sold.
        new("ls50-wireless-ii/carbon-black", "LS50 Wireless II", "Carbon Black", 0x4C5051, 0x464645, 0xA97051, 0x81472B),
        new("ls50-wireless-ii/mineral-white", "LS50 Wireless II", "Mineral White", 0xEFEDE5, 0x4D4B49, 0xA97051, 0x81472B),
        new("ls50-wireless-ii/sand-shell", "LS50 Wireless II", "Sand Shell", 0xD9CFC5, 0x434040, 0xD0B29B, 0xA98564),
        new("ls50-wireless-ii/moss-green", "LS50 Wireless II", "Moss Green", 0x5D6B5C, 0x423F3F, 0xD0B29A, 0xA98664),
        new("ls50-wireless-ii/titanium-grey", "LS50 Wireless II", "Titanium Grey", 0x9D9895, 0x4C4646, 0x963F39, 0x553430),
        new("ls50-wireless-ii/crimson-red-special-edition", "LS50 Wireless II", "Crimson Red Special Edition", 0x9F2C2D, 0x4B3F40, 0x6C6561, 0x4C3E3C),

        // LS60 Wireless
        new("ls60-wireless/mineral-white", "LS60 Wireless", "Mineral White", 0xE7E3E2, 0x25282B, 0xC3BDB9, 0xA7875C),
        new("ls60-wireless/carbon-black", "LS60 Wireless", "Carbon Black", 0x3D3D3D, 0x3A3A3A, 0x6E6D6C, 0x4A4540),
        new("ls60-wireless/titanium-grey", "LS60 Wireless", "Titanium Grey", 0x6F6E6D, 0x26292C, 0xC4A385, 0xA98758),
        new("ls60-wireless/royal-blue", "LS60 Wireless", "Royal Blue", 0x3A527B, 0x25282D, 0xD3B396, 0xAA8958),

        // XIO soundbar. Its drivers sit behind the grille, so the icon uses darker shades of it.
        new("xio/silver-grey", "XIO", "Silver Grey", 0x9F9F97, 0x72726C, 0x888881, 0x72726C),
        new("xio/slate-black", "XIO", "Slate Black", 0x34312C, 0x25231F, 0x2C2A25, 0x25231F),
    ];

    /// <summary>
    /// The app's own colors when no finish is picked, also those of Assets/AppIcon.ico: LS60 Wireless Royal Blue,
    /// which sits well next to the default blue accent of Windows and macOS.
    /// </summary>
    public static readonly KefFinish Default = All.Single(f => f.Id == "ls60-wireless/royal-blue");

    /// <summary>The finishes of a model by its display name, or every finish when the model is not in the table.</summary>
    public static IReadOnlyList<KefFinish> For(string? model)
    {
        var own = All.Where(f => f.Model == model).ToArray();
        return own.Length > 0 ? own : All;
    }

    /// <summary>
    /// The saved finish, unless it belongs to another known model (the speaker was replaced).
    /// Before the model is known the saved finish is used as is.
    /// </summary>
    public static KefFinish? Find(string? id, string? model)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var finish = All.FirstOrDefault(f => f.Id == id);
        if (finish == null) return null;
        return string.IsNullOrEmpty(model) || For(model).Contains(finish) ? finish : null;
    }
}
