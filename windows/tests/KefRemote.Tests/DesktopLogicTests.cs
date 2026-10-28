using KefRemote.Core;
using KefRemote.Desktop;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Perceptual key steps, shared display text and the hotkey coordinator's OSD rules.</summary>
public class DesktopLogicTests
{
    [Theory]
    [InlineData(0, 1, 12)]
    [InlineData(12, 1, 15)]
    [InlineData(13, 1, 15)]
    [InlineData(59, 1, 65)]
    [InlineData(65, 1, 69)]
    [InlineData(98, 1, 100)]
    [InlineData(15, -1, 12)]
    [InlineData(12, -1, 0)]
    [InlineData(5, -1, 0)]
    [InlineData(70, -1, 66)]
    [InlineData(67, -1, 65)]
    public void PerceptualSteps(int current, int direction, int expected) =>
        Assert.Equal(expected, VolumeSteps.NextPerceptual(current, direction));

    [Fact]
    public void PerceptualStepRespectsLimit()
    {
        Assert.Equal(30, VolumeKeyAccumulator.NextTarget(29, 1, VolumeSteps.Perceptual, 30));
        Assert.Equal(25, VolumeKeyAccumulator.NextTarget(29, -1, VolumeSteps.Perceptual, 30));
    }

    [Theory]
    [InlineData("USB-C / PC", "USB-C")]
    [InlineData("TV (HDMI eARC)", "TV")]
    [InlineData("Wi-Fi", "Wi-Fi")]
    public void ShortLabels(string label, string expected) => Assert.Equal(expected, SpeakerPresentation.ShortLabel(label));

    [Theory]
    [InlineData("Wi-Fi", "로")]
    [InlineData("USB-C", "로")]
    [InlineData("Optical", "로")]
    [InlineData("TV (HDMI eARC)", "로")]
    [InlineData("Line", "으로")]
    [InlineData("거실", "로")]
    [InlineData("책상 스피커", "로")]
    [InlineData("서재방", "으로")]
    [InlineData("20", "으로")]
    [InlineData("23", "으로")]
    [InlineData("25", "로")]
    [InlineData("7", "로")]
    public void KoreanParticle(string word, string expected) => Assert.Equal(expected, Korean.Ro(word));

    [Fact]
    public void KoreanWordsStayWhole()
    {
        const string wj = "\u2060";
        Assert.Equal($"음{wj}소{wj}거 해{wj}제", Korean.KeepWords("음소거 해제"));
        Assert.Equal($"Wi-Fi{wj}로", Korean.KeepWords("Wi-Fi로"));
        Assert.Equal("Ctrl + Alt", Korean.KeepWords("Ctrl + Alt"));
        Assert.Equal("", Korean.KeepWords(""));
    }

    [Fact]
    public void VolumeKeysHint()
    {
        Assert.Equal("Ctrl + Alt + ↑ ↓", SpeakerPresentation.VolumeKeysHint(HotkeyBinding.Defaults()));
        var off = HotkeyBinding.Defaults();
        off[0].Key = 0;
        Assert.Null(SpeakerPresentation.VolumeKeysHint(off));
    }

    [Fact]
    public async Task PresentationFollowsPhase()
    {
        long now = 10_000;
        var f = new FakeDriver { Info = new("책상 스피커", "LSX II LT", "1") };
        var c = new SpeakerController { Clock = () => now, Delay = (_, _) => Task.CompletedTask };
        Assert.Equal("스피커를 설정하세요", SpeakerPresentation.Subtitle(c, configured: false));
        await c.ConnectAsync(f);
        Assert.Equal(("LSX II LT", StatusGlyph.On), (SpeakerPresentation.Subtitle(c, true), SpeakerPresentation.Glyph(c)));
        Assert.Equal("책상 스피커\n음량 20, USB", SpeakerPresentation.Tooltip(c, true));
        f.Broken = true;
        await c.PollAsync();
        Assert.Equal(("연결 끊김", StatusGlyph.Offline), (SpeakerPresentation.Subtitle(c, true), SpeakerPresentation.Glyph(c)));
        Assert.True(SpeakerPresentation.SubtitleIsError(c, true));
        f.Broken = false;
        f.Power = PowerState.Standby;
        await c.PollAsync();
        Assert.Equal(("대기", StatusGlyph.Standby), (SpeakerPresentation.Subtitle(c, true), SpeakerPresentation.Glyph(c)));
    }

    sealed class RecordingOsd : IVolumeOsd
    {
        public readonly List<OsdContent> Shown = [];
        public void ShowLevel(OsdContent content) => Shown.Add(content);
    }

    [Fact]
    public void DisabledOsdStillShowsProblems()
    {
        var osd = new RecordingOsd();
        var ready = new VolumeSnapshot(Ready: false, Volume: 20, Muted: false, Limit: 30, Status: "스피커가 대기 상태입니다");
        using var coordinator = new HotkeyVolumeCoordinator(() => ready, _ => Task.CompletedTask, _ => Task.CompletedTask,
            () => false, osd, (_, _) => { }) { OsdEnabled = false };
        coordinator.Handle(HotkeyAction.VolumeUp);
        var shown = Assert.Single(osd.Shown);
        Assert.True(shown.IsError);
        Assert.Equal("스피커가 대기 상태입니다", shown.Message);
    }

    [Fact]
    public void DisabledOsdHidesNormalChanges()
    {
        var osd = new RecordingOsd();
        var ready = new VolumeSnapshot(Ready: true, Volume: 20, Muted: false, Limit: 30);
        var scheduled = new List<Action>();
        using var coordinator = new HotkeyVolumeCoordinator(() => ready, _ => Task.CompletedTask, _ => Task.CompletedTask,
            () => false, osd, (_, a) => scheduled.Add(a)) { OsdEnabled = false };
        coordinator.Handle(HotkeyAction.VolumeUp);
        Assert.Empty(osd.Shown);
        Assert.Single(scheduled);
    }
}
