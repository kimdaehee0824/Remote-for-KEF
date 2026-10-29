using System.Globalization;
using KefRemote.Core;
using Xunit;

namespace KefRemote.Tests;

// Loc.Language stays Korean for the whole run (TestLanguage); these only cover the pure parts.
public class LocTests
{
    [Theory]
    [InlineData("ko-KR", UiLanguage.Korean)]
    [InlineData("ja-JP", UiLanguage.Japanese)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("en-GB", UiLanguage.English)]
    [InlineData("de-DE", UiLanguage.English)]
    [InlineData("zh-Hans", UiLanguage.English)]
    public void DisplayLanguagePicksKoreanJapaneseOrEnglish(string culture, UiLanguage expected) =>
        Assert.Equal(expected, Loc.FromCulture(new CultureInfo(culture)));

    [Theory]
    [InlineData("ko", UiLanguage.Korean)]
    [InlineData("EN", UiLanguage.English)]
    [InlineData(" ja ", UiLanguage.Japanese)]
    [InlineData("fr", null)]
    [InlineData(null, null)]
    public void LangOptionAcceptsKoEnJa(string? code, UiLanguage? expected) =>
        Assert.Equal(expected, Loc.Parse(code));
}
