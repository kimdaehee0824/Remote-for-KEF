namespace KefRemote.Desktop;

/// <summary>Korean text helpers for messages built from names and numbers.</summary>
public static class Korean
{
    const char WordJoiner = '⁠';

    /// <summary>"로" or "으로" for the word in front, by how its last syllable is read: "Wi-Fi로", "30으로".</summary>
    public static string Ro(string word)
    {
        string w = word.Trim();
        // "TV (HDMI eARC)" is read as "TV".
        int paren = w.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) w = w[..paren];
        if (w.Length == 0) return "로";
        char last = w[^1];
        if (last is >= '가' and <= '힣')
        {
            // No final consonant, or ㄹ, takes 로.
            int final = (last - 0xAC00) % 28;
            return final is 0 or 8 ? "로" : "으로";
        }
        // 영, 삼, 육, 십, 백, 천, 만 end in a consonant other than ㄹ.
        if (char.IsAsciiDigit(last)) return last is '0' or '3' or '6' ? "으로" : "로";
        // Latin names: "Stream", "Line" (silent e) end in a nasal; "Optical", "Wi-Fi", "USB-C" do not.
        if (last is 'e' or 'E' && w.Length > 1) last = w[^2];
        return last is 'm' or 'n' or 'M' or 'N' ? "으로" : "로";
    }

    /// <summary>
    /// Keeps Korean words whole when text wraps. Unicode line breaking allows a break between any two Hangul
    /// syllables, so "음소거" can end up split across lines. A word joiner next to every Hangul syllable inside a
    /// word leaves only the spaces as break points, the way Korean is usually set.
    /// </summary>
    public static string KeepWords(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var builder = new System.Text.StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (i > 0 && (IsHangul(c) || IsHangul(text[i - 1])) && !char.IsWhiteSpace(c) && !char.IsWhiteSpace(text[i - 1]))
                builder.Append(WordJoiner);
            builder.Append(c);
        }
        return builder.ToString();
    }

    static bool IsHangul(char c) => c is >= '가' and <= '힣';
}
