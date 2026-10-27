using KefRemote.Core;

namespace KefRemote.Drivers.Kef;

public sealed record KefModel(string Id, string DisplayName, IReadOnlyList<string> Sources, bool HardwareVerified);

/// <summary>
/// W2 platform models. Source lists follow EvotecIT/homeassistant-kef (const.py, MODERN_MODEL_SOURCE_MAP).
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
    ];

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LSX2LT"] = "LSXIILT",
        ["LSX2"] = "LSXII",
        ["LS50WII"] = "LS50W2",
        ["LS60W"] = "LS60",
    };

    /// <summary>
    /// Some firmware reports an internal code as modelName (LSX II LT reports "SP4077"),
    /// so the release text prefix ("LSXIILT_V2186") is used as well.
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
