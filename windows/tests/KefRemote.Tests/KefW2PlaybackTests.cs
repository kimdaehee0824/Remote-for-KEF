using System.Text.Json.Nodes;
using KefRemote.Core;
using KefRemote.Drivers.Kef;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Now playing, player control and the event queue of the KEF W2 driver.</summary>
public class KefW2PlaybackTests
{
    static async Task<KefW2Driver> Connected(FakeKefDevice device, string password = "")
    {
        var driver = new KefW2Driver(new DriverOptions("192.168.0.14", password), device.Handler);
        await driver.ConnectAsync();
        return driver;
    }

    [Fact]
    public void ParsesSpotifyCapturedOnLsxIILt()
    {
        var p = KefW2Protocol.ParsePlayback(FakeKefDevice.PlayerData, """[{"i64_":49876,"type":"i64_"}]""");
        Assert.Equal(PlaybackStatus.Playing, p.Status);
        Assert.Equal(("Love Me Not", "Ravyn Lenae", "Bird's Eye"), (p.Title, p.Artist, p.Album));
        Assert.Equal("https://i.scdn.co/image/ab67616d0000b2730e62daaea2c7b94053e1c142", p.ArtworkUrl);
        Assert.Equal("Spotify", p.Service);
        Assert.Equal((213466L, 49876L), (p.DurationMs, p.PositionMs));
        Assert.True(p.CanTogglePause && p.CanNext && p.CanPrevious);
        Assert.True(p.HasTrack);
    }

    [Fact]
    public void PhysicalInputHasNoControlsOrArtwork()
    {
        const string usb = """[{"trackRoles":{"title":"usb","icon":"skin:iconUsb","mediaData":{"metaData":{"serviceID":"usb"}}},"controls":{"pause":false,"next_":false,"previous":false}}]""";
        var p = KefW2Protocol.ParsePlayback(usb, """[{"type":"i64_","i64_":-1}]""");
        Assert.Equal(PlaybackStatus.Stopped, p.Status);
        Assert.Null(p.ArtworkUrl);
        Assert.Equal(-1, p.PositionMs);
        Assert.False(p.CanTogglePause || p.CanNext || p.CanPrevious);
        Assert.False(p.HasTrack);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("null")]
    public void EmptyPlayerIsNothing(string json) =>
        Assert.False(KefW2Protocol.ParsePlayback(json, null).HasTrack);

    [Fact]
    public void ControlNeverSendsBarePlay()
    {
        foreach (var command in Enum.GetValues<PlaybackCommand>())
            Assert.NotEqual("play", KefW2Protocol.ControlValue(command)["control"]!.GetValue<string>());
        Assert.Equal("""{"control":"pause"}""", KefW2Protocol.ControlValue(PlaybackCommand.TogglePause).ToJsonString());
    }

    [Fact]
    public void QueueRequests()
    {
        Assert.Equal(
            """{"subscribe":[{"path":"player:volume","type":"itemWithValue"}],"unsubscribe":[]}""",
            KefW2Protocol.ModifyQueueBody(["player:volume"]));
        Assert.Equal(
            "http://192.168.0.14/api/event/pollQueue?queueId=%7Bcaa6b0ae%7D&timeout=10",
            KefW2Protocol.PollQueueUrl("192.168.0.14", "{caa6b0ae}", 10));
        Assert.Equal("{caa6b0ae-4a71-4647-8bf7-c2036842e974}", KefW2Protocol.ReadQueueId("\"{caa6b0ae-4a71-4647-8bf7-c2036842e974}\""));
        Assert.Throws<SpeakerException>(() => KefW2Protocol.ReadQueueId("[]"));
    }

    static DeviceChanges Changes(string json) => KefW2Protocol.ParseChanges(json, KefW2Driver.VolumePath, KefW2Driver.MutePath,
        new HashSet<string> { KefW2Driver.SourcePath, KefW2Driver.PowerPath }, new HashSet<string> { "settings:/deviceName" });

    [Fact]
    public void SortsEvents()
    {
        // Captured on LSX II LT: playTime only, which this app doesn't subscribe to.
        Assert.False(Changes("""[{"itemValue":{"i64_":50526,"type":"i64_"},"rowsEvents":[],"path":"player:player/data/playTime","itemType":"update"}]""").Any);
        Assert.False(Changes("[]").Any);

        var c = Changes("""
            [{"path":"player:volume","itemType":"update","itemValue":{"type":"i32_","i32_":30}},
             {"path":"player:volume","itemType":"update","itemValue":{"type":"i32_","i32_":31}},
             {"path":"settings:/mediaPlayer/mute","itemType":"update","itemValue":{"type":"bool_","bool_":true}},
             {"path":"player:player/data","itemType":"update","itemValue":{"state":"paused"}}]
            """);
        Assert.Equal(new DeviceChanges { Volume = 31, Muted = true, Playback = true }, c);

        Assert.True(Changes("""[{"path":"settings:/kef/play/physicalSource","itemType":"update","itemValue":{"type":"kefPhysicalSource","kefPhysicalSource":"wifi"}}]""").State);
        Assert.True(Changes("""[{"path":"settings:/deviceName","itemType":"update","itemValue":{"type":"string_","string_":"Desk"}}]""").Device);
        // A value that can't be read asks for a full read.
        Assert.Equal(new DeviceChanges { State = true }, Changes("""[{"path":"player:volume","itemType":"update"}]"""));
        Assert.Throws<SpeakerException>(() => Changes("""{"error":{"message":"no queue"}}"""));
    }

    [Fact]
    public async Task ReadsWhatIsPlaying()
    {
        var device = new FakeKefDevice { Source = "wifi" };
        using var driver = await Connected(device);
        Assert.True(driver.Capabilities.HasFlag(DriverCapabilities.Playback | DriverCapabilities.Changes));
        Assert.True(driver.SourceHasPlayback("wifi"));
        Assert.False(driver.SourceHasPlayback("usb"));
        var p = await driver.ReadPlaybackAsync();
        Assert.Equal(("Love Me Not", 49876L), (p.Title, p.PositionMs));
        Assert.Equal(0, device.Posts);
    }

    [Fact]
    public async Task PlayerControlIsSignedWithTheActivateRole()
    {
        var device = new FakeKefDevice("secret") { Source = "wifi" };
        using var driver = await Connected(device, "secret");
        await driver.SendPlaybackAsync(PlaybackCommand.TogglePause);
        await driver.SendPlaybackAsync(PlaybackCommand.Next);
        await driver.SendPlaybackAsync(PlaybackCommand.Previous);
        Assert.Equal(["pause", "next", "previous"], device.Controls);
        Assert.Equal("paused", device.PlayState);
        var (request, body) = device.Handler.Requests.Last(r => r.Request.Method == HttpMethod.Post);
        Assert.True(request.Headers.Contains("Authorization"));
        Assert.Equal("activate", JsonNode.Parse(body!)!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task EventQueueSubscribesOnceUnsignedAndResubscribesAfterAFailure()
    {
        var device = new FakeKefDevice("secret");
        using var driver = await Connected(device, "secret");
        device.Events.Enqueue("""[{"path":"player:volume","itemType":"update","itemValue":{"type":"i32_","i32_":33}}]""");

        Assert.Equal(new DeviceChanges { Volume = 33 }, await driver.WaitForChangesAsync());
        Assert.Equal(DeviceChanges.None, await driver.WaitForChangesAsync());
        Assert.Equal(1, device.QueuesCreated);
        Assert.Contains(KefW2Driver.SourcePath, device.Subscriptions);
        Assert.Contains(KefW2Protocol.PlayerDataPath, device.Subscriptions);
        Assert.DoesNotContain(KefW2Protocol.PlayTimePath, device.Subscriptions);
        Assert.All(device.Handler.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.StartsWith("/api/event/")),
            r => Assert.False(r.Request.Headers.Contains("Authorization")));
        Assert.Equal(0, device.Posts);

        device.QueueBroken = true;
        await Assert.ThrowsAsync<SpeakerException>(() => driver.WaitForChangesAsync());
        device.QueueBroken = false;
        Assert.Equal(DeviceChanges.None, await driver.WaitForChangesAsync());
        Assert.Equal(2, device.QueuesCreated);
    }
}
