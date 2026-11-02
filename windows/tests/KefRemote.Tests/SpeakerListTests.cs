using System.Text.Json;
using KefRemote.Core;
using KefRemote.Desktop;
using Xunit;

namespace KefRemote.Tests;

/// <summary>The list of registered speakers in the settings, and the move from the single speaker of 0.4.</summary>
public class SpeakerListTests
{
    static AppSettings Legacy() => new()
    {
        Host = " 192.168.0.20 ",
        ProtectedPassword = "cipher",
        AppVolumeLimit = 40,
        ApproachFromAbove = true,
        SpeakerFinish = "lsx-ii-lt/sage-green",
        SpeakerModels = { ["192.168.0.20"] = "LSX II LT", ["192.168.0.99"] = "LS60 Wireless" },
        LastInputs = { ["192.168.0.20"] = "usb" },
    };

    [Fact]
    public void The_single_speaker_of_0_4_becomes_the_first_entry()
    {
        var settings = Legacy();
        Assert.True(SpeakerList.Migrate(settings));
        var entry = Assert.Single(settings.Speakers!);
        Assert.Equal(("192.168.0.20", "cipher", 40, true), (entry.Host, entry.ProtectedPassword, entry.AppVolumeLimit, entry.ApproachFromAbove));
        Assert.Equal(("lsx-ii-lt/sage-green", "LSX II LT", "usb"), (entry.Finish, entry.PickedModel, entry.LastInput));
        Assert.Equal(entry.Id, settings.SelectedSpeaker);
        Assert.Same(entry, SpeakerList.Selected(settings));
        // An older version opened again still finds its speaker.
        Assert.Equal(" 192.168.0.20 ", settings.Host);
        Assert.Equal(40, settings.AppVolumeLimit);
    }

    [Fact]
    public void Migration_runs_once()
    {
        var settings = Legacy();
        SpeakerList.Migrate(settings);
        settings.Speakers!.Clear();
        Assert.False(SpeakerList.Migrate(settings));
        Assert.Empty(settings.Speakers);
        Assert.False(settings.IsConfigured);
    }

    [Fact]
    public void Nothing_set_up_gives_an_empty_list()
    {
        var settings = new AppSettings();
        Assert.True(SpeakerList.Migrate(settings));
        Assert.Empty(settings.Speakers!);
        Assert.Null(SpeakerList.Selected(settings));
        Assert.Null(SpeakerList.Next(settings));
    }

    [Fact]
    public void A_limit_out_of_range_is_clamped_when_moved()
    {
        var settings = Legacy();
        settings.AppVolumeLimit = 0;
        SpeakerList.Migrate(settings);
        Assert.Equal(1, settings.Speakers![0].AppVolumeLimit);
    }

    [Fact]
    public void Adding_keeps_the_selection_and_the_first_is_selected()
    {
        var settings = new AppSettings();
        SpeakerList.Migrate(settings);
        var desk = SpeakerList.Add(settings, " 192.168.0.20", null);
        Assert.Equal("192.168.0.20", desk.Host);
        Assert.Equal(desk.Id, settings.SelectedSpeaker);
        var living = SpeakerList.Add(settings, "192.168.0.21", "cipher");
        Assert.Equal(desk.Id, settings.SelectedSpeaker);
        Assert.Equal(30, living.AppVolumeLimit);
        Assert.NotEqual(desk.Id, living.Id);
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void Next_wraps_around()
    {
        var settings = new AppSettings();
        var a = SpeakerList.Add(settings, "192.168.0.20", null);
        Assert.Null(SpeakerList.Next(settings));
        var b = SpeakerList.Add(settings, "192.168.0.21", null);
        var c = SpeakerList.Add(settings, "192.168.0.22", null);
        Assert.Same(b, SpeakerList.Next(settings));
        settings.SelectedSpeaker = c.Id;
        Assert.Same(a, SpeakerList.Next(settings));
    }

    [Fact]
    public void Removing_the_selected_one_hands_over_to_the_one_before()
    {
        var settings = new AppSettings();
        var a = SpeakerList.Add(settings, "192.168.0.20", null);
        var b = SpeakerList.Add(settings, "192.168.0.21", null);
        var c = SpeakerList.Add(settings, "192.168.0.22", null);
        settings.SelectedSpeaker = b.Id;
        Assert.True(SpeakerList.Remove(settings, b.Id));
        Assert.Equal(a.Id, settings.SelectedSpeaker);
        Assert.True(SpeakerList.Remove(settings, a.Id));
        Assert.Equal(c.Id, settings.SelectedSpeaker);
        Assert.True(SpeakerList.Remove(settings, c.Id));
        Assert.Null(settings.SelectedSpeaker);
        Assert.False(SpeakerList.Remove(settings, c.Id));
    }

    [Fact]
    public void Removing_another_one_keeps_the_selection()
    {
        var settings = new AppSettings();
        var a = SpeakerList.Add(settings, "192.168.0.20", null);
        var b = SpeakerList.Add(settings, "192.168.0.21", null);
        SpeakerList.Remove(settings, b.Id);
        Assert.Equal(a.Id, settings.SelectedSpeaker);
    }

    [Fact]
    public void A_selection_that_no_longer_exists_falls_back_to_the_first()
    {
        var settings = new AppSettings();
        var a = SpeakerList.Add(settings, "192.168.0.20", null);
        settings.SelectedSpeaker = "gone";
        Assert.Same(a, SpeakerList.Selected(settings));
        SpeakerList.Migrate(settings);
        Assert.Equal(a.Id, settings.SelectedSpeaker);
    }

    [Fact]
    public void The_same_address_is_found_whatever_the_spacing()
    {
        var settings = new AppSettings();
        var a = SpeakerList.Add(settings, "192.168.0.20", null);
        Assert.Same(a, SpeakerList.WithHost(settings, " 192.168.0.20 "));
        Assert.Null(SpeakerList.WithHost(settings, "192.168.0.20", exceptId: a.Id));
        Assert.Null(SpeakerList.WithHost(settings, "192.168.0.21"));
    }

    [Fact]
    public void Display_name_prefers_the_reported_name()
    {
        var entry = new SpeakerEntry { Host = "192.168.0.20" };
        Assert.Equal("192.168.0.20", SpeakerList.DisplayName(entry));
        entry.Name = "거실 스피커";
        Assert.Equal("거실 스피커", SpeakerList.DisplayName(entry));
        Assert.Equal("새 스피커", SpeakerList.DisplayName(new SpeakerEntry()));
    }

    [Fact]
    public void The_list_survives_a_save()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kef-speakers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(folder);
            var settings = new AppSettings();
            var a = SpeakerList.Add(settings, "192.168.0.20", "cipher");
            a.Name = "책상 스피커";
            a.Model = "LSX II LT";
            SpeakerList.Add(settings, "192.168.0.21", null).AppVolumeLimit = 20;
            store.Save(settings);
            var back = store.Load();
            Assert.False(SpeakerList.Migrate(back));
            Assert.Equal(2, back.Speakers!.Count);
            Assert.Equal((a.Id, "책상 스피커", "LSX II LT", "cipher"), (back.Speakers[0].Id, back.Speakers[0].Name, back.Speakers[0].Model, back.Speakers[0].ProtectedPassword));
            Assert.Equal(20, back.Speakers[1].AppVolumeLimit);
            Assert.Equal(a.Id, back.SelectedSpeaker);
            Assert.DoesNotContain("IsConfigured", File.ReadAllText(Path.Combine(folder, "settings.json")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task List_status_reads_volume_or_state()
    {
        long now = 10_000;
        var f = new FakeDriver { Info = new("거실 스피커", "LS60 Wireless", "1") };
        var c = new SpeakerController { Clock = () => now, Delay = (_, _) => Task.CompletedTask, ListenForChanges = false };
        Assert.Equal("확인하는 중", SpeakerPresentation.ListStatus(c, unreachable: false));
        Assert.Equal("연결 안 됨", SpeakerPresentation.ListStatus(c, unreachable: true));
        await c.ConnectAsync(f);
        Assert.Equal("음량 20", SpeakerPresentation.ListStatus(c, false));
        f.Power = PowerState.Standby;
        await c.PollAsync();
        Assert.Equal("대기", SpeakerPresentation.ListStatus(c, false));
        f.Broken = true;
        await c.PollAsync();
        Assert.Equal("연결 안 됨", SpeakerPresentation.ListStatus(c, false));
    }

    [Fact]
    public void Tooltip_uses_the_last_known_name_before_connecting()
    {
        var c = new SpeakerController();
        Assert.StartsWith("거실 스피커\n", SpeakerPresentation.Tooltip(c, true, "거실 스피커"));
        Assert.StartsWith("Remote for KEF\n", SpeakerPresentation.Tooltip(c, false, "거실 스피커"));
    }
}
