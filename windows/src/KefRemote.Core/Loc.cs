using System.Globalization;

namespace KefRemote.Core;

public enum UiLanguage { Korean, English, Japanese }

/// <summary>
/// UI text in Korean, English and Japanese. The language follows the Windows display language
/// (Korean and Japanese as is, anything else English) unless --lang picks one at startup.
/// Each string keeps its three translations next to the code that shows it.
/// </summary>
public static class Loc
{
    public static UiLanguage Language { get; set; } = FromCulture(CultureInfo.CurrentUICulture);

    public static UiLanguage FromCulture(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "ko" => UiLanguage.Korean,
        "ja" => UiLanguage.Japanese,
        _ => UiLanguage.English,
    };

    /// <summary>"ko", "en" or "ja"; null for anything else.</summary>
    public static UiLanguage? Parse(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "ko" => UiLanguage.Korean,
        "en" => UiLanguage.English,
        "ja" => UiLanguage.Japanese,
        _ => null,
    };

    /// <summary>The language as a BCP 47 tag, for XAML's Language so Han characters get Korean or Japanese glyphs.</summary>
    public static string Tag => Language switch { UiLanguage.Korean => "ko-KR", UiLanguage.Japanese => "ja-JP", _ => "en-US" };

    public static string T(string ko, string en, string ja) => Language switch
    {
        UiLanguage.Korean => ko,
        UiLanguage.Japanese => ja,
        _ => en,
    };
}
