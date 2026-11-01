using KefRemote.Core;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Change reports from the device and the now-playing player.</summary>
public class SpeakerControllerChangesTests
{
    long now = 10_000;

    async Task<SpeakerController> Connected(FakeDriver f, bool playback = false)
    {
        f.Capabilities |= DriverCapabilities.Changes | DriverCapabilities.Playback;
        var c = new SpeakerController { Clock = () => now, Delay = (_, _) => Task.CompletedTask, PlaybackEnabled = playback };
        Assert.True(await c.ConnectAsync(f));
        return c;
    }

    static PlaybackInfo Song(PlaybackStatus status = PlaybackStatus.Playing) => new()
    {
        Status = status,
        Title = "Love Me Not",
        Artist = "Ravyn Lenae",
        DurationMs = 213_466,
        PositionMs = 49_876,
        CanTogglePause = true,
        CanNext = true,
        CanPrevious = true,
    };

    [Fact]
    public async Task ListensAfterConnectAndSlowsPollingToAHeartbeat()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        Assert.Equal(1, f.ChangeCalls);
        Assert.False(c.ChangesActive);
        Assert.Equal(1500, c.NextPollDelay(true).TotalMilliseconds);

        f.Report(DeviceChanges.None);
        Assert.True(c.ChangesActive);
        Assert.Equal(2, f.ChangeCalls);
        Assert.Equal(3000, c.NextPollDelay(true).TotalMilliseconds);
        Assert.Equal(10000, c.NextPollDelay(false).TotalMilliseconds);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task EchoOfOwnWriteCostsNoRead()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Report(DeviceChanges.None);
        await c.StepVolumeAsync(1);
        int reads = f.Reads;

        f.Report(new DeviceChanges { Volume = 21 });
        Assert.Equal(reads, f.Reads);
        Assert.Equal(21, c.State.Volume);
    }

    [Fact]
    public async Task ChangeMadeElsewhereShowsAtOnce()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Volume = 25;
        f.Report(new DeviceChanges { Volume = 25 });
        Assert.Equal(25, c.State.Volume);

        f.Source = "wifi";
        f.Report(new DeviceChanges { State = true });
        Assert.Equal("wifi", c.State.SourceId);

        f.Muted = true;
        f.Report(new DeviceChanges { Muted = true });
        Assert.True(c.State.Muted);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task ChangesWhileBusyAreLookedAtAfterwards()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Gate = new TaskCompletionSource<bool>();
        var poll = c.PollAsync();
        Assert.True(c.Busy);
        int reads = f.Reads;

        f.Report(new DeviceChanges { State = true });
        Assert.Equal(reads, f.Reads);

        f.Gate.SetResult(true);
        await poll;
        Assert.Equal(reads + 1, f.Reads);
        Assert.False(c.Busy);
    }

    [Fact]
    public async Task DeviceReportRefreshesTheVolumeLimit()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Info = f.Info with { DeviceVolumeLimit = 20 };
        f.Report(new DeviceChanges { Device = true });
        Assert.Equal(20, c.EffectiveLimit);
    }

    [Fact]
    public async Task FailedReportsFallBackToPollingAndRetry()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Report(DeviceChanges.None);
        int reads = f.Reads, calls = f.ChangeCalls;

        f.FailChanges();
        Assert.False(c.ChangesActive);
        Assert.Equal(reads + 1, f.Reads);
        Assert.Equal(calls + 1, f.ChangeCalls);
        Assert.Equal(1500, c.NextPollDelay(true).TotalMilliseconds);
        Assert.True(c.IsConnected);
    }

    [Fact]
    public async Task SpeakerGoneShowsOfflineWithoutWaitingForThePoll()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Report(DeviceChanges.None);
        f.Broken = true;
        f.FailChanges();
        Assert.Equal(SpeakerPhase.Offline, c.Phase);
    }

    [Fact]
    public async Task InvalidateKeepsListeningDisconnectStops()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        var wait = f.ChangeWaits.Peek();
        c.Invalidate("test");
        Assert.False(wait.Task.IsCompleted);

        c.Disconnect("test");
        Assert.True(wait.Task.IsCanceled);
        Assert.Equal(1, f.ChangeCalls);
        Assert.False(c.ChangesActive);
    }

    [Fact]
    public async Task NoListeningWithoutTheCapability()
    {
        var f = new FakeDriver();
        var c = new SpeakerController { Clock = () => now, Delay = (_, _) => Task.CompletedTask };
        Assert.True(await c.ConnectAsync(f));
        Assert.Equal(0, f.ChangeCalls);
    }

    [Fact]
    public async Task PlaybackShowsOnlyOnAStreamingInput()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song() };
        var c = await Connected(f, playback: true);
        Assert.True(c.ShowsPlayback);
        Assert.Equal("Love Me Not", c.Playback.Title);

        f.Source = "usb";
        f.Report(new DeviceChanges { State = true });
        Assert.False(c.ShowsPlayback);
        Assert.Equal(PlaybackInfo.None, c.Playback);
    }

    [Fact]
    public async Task PlaybackOffReadsNothing()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song() };
        var c = await Connected(f);
        await c.PollAsync();
        f.Report(new DeviceChanges { Playback = true });
        Assert.Equal(0, f.PlaybackReads);
        Assert.False(c.ShowsPlayback);

        c.PlaybackEnabled = true;
        Assert.True(c.ShowsPlayback);
        c.PlaybackEnabled = false;
        Assert.False(c.ShowsPlayback);
    }

    [Fact]
    public async Task PlaybackReportIsRead()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song() };
        var c = await Connected(f, playback: true);
        f.Report(DeviceChanges.None);
        f.Playing = Song() with { Title = "Next song" };
        f.Report(new DeviceChanges { Playback = true });
        Assert.Equal("Next song", c.Playback.Title);
    }

    [Fact]
    public async Task PausedPlayerIsNotReadWithEveryHeartbeat()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song(PlaybackStatus.Paused) };
        var c = await Connected(f, playback: true);
        f.Report(DeviceChanges.None);
        int reads = f.PlaybackReads;
        await c.PollAsync();
        Assert.Equal(reads, f.PlaybackReads);

        f.Playing = Song();
        f.Report(new DeviceChanges { Playback = true });
        await c.PollAsync();
        Assert.Equal(reads + 2, f.PlaybackReads);
    }

    [Fact]
    public async Task TogglePauseShowsAtOnceAndIsConfirmed()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song(), PlaybackLagReads = 2 };
        var c = await Connected(f, playback: true);
        var states = new List<PlaybackStatus>();
        c.Changed += () => states.Add(c.Playback.Status);

        await c.PlaybackAsync(PlaybackCommand.TogglePause);
        Assert.Equal([PlaybackCommand.TogglePause], f.PlaybackCommands);
        Assert.Equal(PlaybackStatus.Paused, c.Playback.Status);
        // Never flips back while the speaker catches up.
        Assert.DoesNotContain(PlaybackStatus.Playing, states.SkipWhile(s => s == PlaybackStatus.Playing));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task NextIsConfirmedByTheNewTrack()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song(), PlaybackLagReads = 1 };
        var c = await Connected(f, playback: true);
        await c.PlaybackAsync(PlaybackCommand.Next);
        Assert.Equal("Love Me Not (Next)", c.Playback.Title);
    }

    [Fact]
    public async Task PlaybackIsReadAgainAfterSleepCutsAConfirmationShort()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song() };
        var c = await Connected(f, playback: true);
        var stuck = new TaskCompletionSource();
        c.Delay = (ms, _) => ms == 300 ? stuck.Task : Task.CompletedTask;
        await c.PlaybackAsync(PlaybackCommand.Next);

        c.Invalidate("sleep");
        await c.PollAsync();
        f.Playing = Song() with { Title = "After sleep" };
        await c.RefreshPlaybackAsync();
        Assert.Equal("After sleep", c.Playback.Title);
    }

    [Fact]
    public async Task RejectedPlaybackCommandKeepsTheConnection()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song(), RejectPlayback = true };
        var c = await Connected(f, playback: true);
        await c.PlaybackAsync(PlaybackCommand.Next);
        Assert.NotNull(c.Notice);
        Assert.False(c.NoticeIsError);
        Assert.True(c.CanControl);
    }

    [Fact]
    public async Task PositionCountsOnWhilePlaying()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song() with { PositionMs = 1000, DurationMs = 5000 } };
        var c = await Connected(f, playback: true);
        now += 1500;
        Assert.Equal(2500, c.PlaybackPositionMs);
        now += 10_000;
        Assert.Equal(5000, c.PlaybackPositionMs);

        f.Playing = f.Playing with { Status = PlaybackStatus.Paused, PositionMs = 3000 };
        await c.RefreshPlaybackAsync();
        now += 5000;
        Assert.Equal(3000, c.PlaybackPositionMs);
    }

    [Fact]
    public async Task DisconnectClearsPlayback()
    {
        var f = new FakeDriver { Source = "wifi", Playing = Song() };
        var c = await Connected(f, playback: true);
        c.Disconnect("test");
        Assert.Equal(PlaybackInfo.None, c.Playback);
        Assert.False(c.ShowsPlayback);
    }
}
