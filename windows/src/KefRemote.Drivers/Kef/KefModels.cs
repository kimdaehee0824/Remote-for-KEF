using KefRemote.Core;

namespace KefRemote.Drivers.Kef;

public sealed record KefModel(string Id, string DisplayName, IReadOnlyList<string> Sources, bool HardwareVerified);

/// <summary>
/// W2 platform models. Source lists follow EvotecIT/homeassistant-kef (const.py, MODERN_MODEL_SOURCE_MAP),
/// and KEF's specifications for LS LUXE, which came out after it.
/// Only LSX II LT was verified on real hardware for this project (usb and wifi switching).
/// </summary>
public static class KefModels
{
    static readonly string[] DefaultSources = ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog", "usb"];

    static readonly KefModel[] Known =
    [
        new("LSXIILT", "LSX II LT", ["usb", "wifi", "bluetooth", "tv", "optical"], true),
        new("LSXII", "LSX II", ["wifi", "bluetooth", "tv", "optical", "analog", "usb"], false),
        new("LS50W2", "LS50 Wireless II", ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog"], false),
        new("LS60", "LS60 Wireless", ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog"], false),
        new("XIO", "XIO", ["wifi", "bluetooth", "tv", "optical"], false),
        new("LSLUXE", "LS LUXE", ["wifi", "bluetooth", "tv", "optical", "coaxial", "analog"], false),
    ];

    /// <summary>
    /// The speakers report the first six characters of the KEF part number as modelName ("SP4077J2CA" is an LSX II LT
    /// in Sage Green). SP4077, SP4041 and SP4083 were seen on real speakers; the other three follow the part numbers
    /// KEF sells them under.
    /// </summary>
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LSX2LT"] = "LSXIILT",
        ["LSX2"] = "LSXII",
        ["LS50WII"] = "LS50W2",
        ["LS60W"] = "LS60",
        ["SP4077"] = "LSXIILT",
        ["SP4041"] = "LSXII",
        ["SP4025"] = "LS50W2",
        ["SP4017"] = "LS60",
        ["SP4083"] = "XIO",
        ["SP4042"] = "LSLUXE",
    };

    /// <summary>Display names of the models in the table, to pick from when a speaker reports one the app does not know.</summary>
    public static IReadOnlyList<string> Names { get; } = Known.Select(m => m.DisplayName).ToArray();

    /// <summary>
    /// The model the speaker is drawn as and whose finishes are listed: the reported one when it is in the table,
    /// otherwise the one picked in settings, otherwise the reported one as is (null before the speaker is connected).
    /// The picked model is not used for anything else; the inputs stay those of the reported model.
    /// </summary>
    public static string? Shown(string? reported, string? picked) =>
        IsKnown(reported) ? reported : IsKnown(picked) ? picked : reported;

    static bool IsKnown(string? name) => name != null && Names.Contains(name);

    /// <summary>
    /// The release text prefix ("LSXIILT_V2186") is used as well, for part numbers not in the table.
    /// </summary>
    public static KefModel Resolve(string modelName, string releaseText)
    {
        foreach (var candidate in new[] { modelName, ReleasePrefix(releaseText) })
        {
            string id = Normalize(candidate);
            if (Aliases.TryGetValue(id, out var alias)) id = alias;
            var known = Known.FirstOrDefault(m => m.Id == id);
            if (known != null) return known;
        }
        string label = string.IsNullOrWhiteSpace(ReleasePrefix(releaseText)) ? modelName : ReleasePrefix(releaseText);
        return new KefModel(Normalize(label), $"KEF {label}", DefaultSources, false);
    }

    public static string SourceLabel(string id) => id switch
    {
        "usb" => "USB-C / PC",
        "wifi" => "Wi-Fi",
        "bluetooth" => "Bluetooth",
        "tv" => "TV (HDMI eARC)",
        "optical" => "Optical",
        "coaxial" => "Coaxial",
        "analog" => "Analog",
        _ => id,
    };

    public static IReadOnlyList<SourceOption> SourceOptions(KefModel model) =>
        model.Sources.Select(s => new SourceOption(s, SourceLabel(s))).ToArray();

    static string ReleasePrefix(string releaseText)
    {
        int i = releaseText.IndexOf("_V", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? releaseText[..i] : "";
    }

    static string Normalize(string s) => s.Replace(" ", "").Replace("-", "").ToUpperInvariant();
}
