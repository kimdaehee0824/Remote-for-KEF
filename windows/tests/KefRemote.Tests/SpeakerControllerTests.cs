using KefRemote.Core;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Safety policy. Ported from the 25 prototype checks and extended for the new commands.</summary>
public class SpeakerControllerTests
{
    long now = 10_000;

    async Task<SpeakerController> Connected(FakeDriver f)
    {
        var c = new SpeakerController { Clock = () => now, Delay = (_, _) => Task.CompletedTask };
        Assert.True(await c.ConnectAsync(f));
        return c;
    }

    [Theory]
    [InlineData(29, 30, 30, 30)]
    [InlineData(29, 40, 30, 30)]
    [InlineData(70, 71, 30, 70)]
    [InlineData(70, 69, 30, 69)]
    [InlineData(0, -1, 30, 0)]
    [InlineData(10, 25, 30, 25)]
    [InlineData(40, 10, 30, 10)]
    public void ClampTarget(int current, int requested, int limit, int expected) =>
        Assert.Equal(expected, SpeakerController.ClampTarget(current, requested, limit));

    [Fact]
    public async Task ConnectAndPollNeverWrite()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        await c.PollAsync();
        Assert.Equal(0, f.Writes);
        Assert.True(c.CanControl);
    }

    [Fact]
    public async Task StepDownIsVerified()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        await c.StepVolumeAsync(-1);
        Assert.Equal((19, 1), (f.Volume, f.Writes));
        Assert.Equal(19, c.State.Volume);
    }

    [Fact]
    public async Task CapSendsNoWrite()
    {
        var f = new FakeDriver { Volume = 30 };
        var c = await Connected(f);
        await c.StepVolumeAsync(1);
        Assert.Equal(0, f.Writes);
        Assert.Contains("상한", c.Notice);
    }

    [Fact]
    public async Task DeviceLimitLowersEffectiveLimit()
    {
        var f = new FakeDriver { Volume = 20, Info = new("T", "F", "1", DeviceVolumeLimit: 22) };
        var c = await Connected(f);
        await c.SetVolumeTargetAsync(28);
        Assert.Equal(22, f.Volume);
    }

    [Fact]
    public async Task AboveCapStartIsShownAsIsAndOnlyLowered()
    {
        var f = new FakeDriver { Volume = 32 };
        var c = await Connected(f);
        Assert.Equal(32, c.State.Volume);
        await c.StepVolumeAsync(1);
        Assert.Equal(0, f.Writes);
        await c.StepVolumeAsync(-1);
        Assert.Equal(31, f.Volume);
    }

    [Fact]
    public async Task DisconnectBlocksAndReconnectDoesNotReplay()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Broken = true;
        await c.StepVolumeAsync(1);
        Assert.False(c.State.Valid);
        Assert.Equal(0, f.Writes);
        f.Broken = false;
        await c.PollAsync();
        Assert.True(c.State.Valid);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task StaleSnapshotBlocksCommand()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        now += SpeakerController.StaleMilliseconds;
        Assert.False(c.CanControl);
        await c.StepVolumeAsync(1);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task CommandsWorkBetweenSlowPolls()
    {
        // Hidden-window polling runs every 4 s plus up to 1.5 s of request time. Input must not drop out in between.
        var f = new FakeDriver();
        var c = await Connected(f);
        now += (long)c.NextPollDelay(windowVisible: false).TotalMilliseconds + 1500;
        Assert.True(c.CanControl);
        await c.StepVolumeAsync(1);
        Assert.Equal((21, 1), (f.Volume, f.Writes));
    }

    [Fact]
    public async Task ExternalSourceChangeAbortsVolume()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Source = "wifi";
        await c.StepVolumeAsync(1);
        Assert.Equal(0, f.Writes);
        Assert.Equal("wifi", c.State.SourceId);
    }

    [Fact]
    public async Task StandbyBlocksCommands()
    {
        var f = new FakeDriver { Power = PowerState.Standby };
        var c = await Connected(f);
        Assert.False(c.CanControl);
        await c.StepVolumeAsync(-1);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task UnconfirmedWriteIsNeverRetried()
    {
        var f = new FakeDriver { IgnoreWrites = true };
        var c = await Connected(f);
        await c.StepVolumeAsync(1);
        Assert.Equal(1, f.Writes);
        Assert.False(c.State.Valid);
    }

    [Fact]
    public async Task VolumeInputWhileBusyMergesIntoLatestTarget()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Gate = new TaskCompletionSource<bool>();
        var first = c.StepVolumeAsync(1);
        var second = c.StepVolumeAsync(1);
        var third = c.SetVolumeTargetAsync(25);
        Assert.Same(second, third);
        f.Gate.SetResult(true);
        await first;
        await third;
        Assert.Equal((2, 25), (f.Writes, f.Volume));
    }

    [Fact]
    public async Task StepsWhileBusyAreSummedAgainstFreshRead()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Gate = new TaskCompletionSource<bool>();
        var first = c.StepVolumeAsync(1);
        var merged = c.StepVolumeAsync(1);
        _ = c.StepVolumeAsync(1);
        f.Volume = 10; // another controller changed it; steps apply to what is read, not to the old value
        f.Gate.SetResult(true);
        await first;
        await merged;
        Assert.Equal((2, 13), (f.Writes, f.Volume));
    }

    [Fact]
    public async Task MergedVolumeIsDroppedAfterFailure()
    {
        var f = new FakeDriver { IgnoreWrites = true };
        var c = await Connected(f);
        f.Gate = new TaskCompletionSource<bool>();
        var first = c.StepVolumeAsync(1);
        var merged = c.SetVolumeTargetAsync(25);
        f.Gate.SetResult(true);
        await first;
        await merged;
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task MergedVolumeIsDroppedOnDisconnect()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Gate = new TaskCompletionSource<bool>();
        var first = c.StepVolumeAsync(1);
        var merged = c.SetVolumeTargetAsync(25);
        c.Disconnect("bye");
        await merged;
        f.Gate.SetResult(true);
        await first;
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task MuteAndSourceWhileBusyAreDropped()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Gate = new TaskCompletionSource<bool>();
        var first = c.StepVolumeAsync(1);
        await c.ToggleMuteAsync();
        await c.SelectSourceAsync("wifi");
        f.Gate.SetResult(true);
        await first;
        Assert.Equal((1, false, "usb"), (f.Writes, f.Muted, f.Source));
    }

    [Fact]
    public async Task InvalidateDuringReadCancelsPendingWrite()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Gate = new TaskCompletionSource<bool>();
        var task = c.StepVolumeAsync(1);
        c.Invalidate("stop");
        f.Gate.SetResult(true);
        await task;
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task DisconnectDuringReadCancelsPendingWrite()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Gate = new TaskCompletionSource<bool>();
        var task = c.SetVolumeTargetAsync(25);
        c.Disconnect("bye");
        f.Gate.SetResult(true);
        await task;
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task SourceSwitchMutesAndRestoresWhenVolumeIsNotHigher()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        await c.SelectSourceAsync("wifi");
        Assert.Equal(("wifi", false, 3), (f.Source, f.Muted, f.Writes));
        Assert.True(c.State.Valid);
    }

    [Fact]
    public async Task SourceSwitchStaysMutedWhenNewInputIsLouder()
    {
        var f = new FakeDriver { VolumePerSource = { ["wifi"] = 28 } };
        var c = await Connected(f);
        await c.SelectSourceAsync("wifi");
        Assert.Equal(("wifi", true, 2), (f.Source, f.Muted, f.Writes));
        Assert.Contains("음소거를 유지", c.Notice);
    }

    [Fact]
    public async Task SourceSwitchKeepsExistingMute()
    {
        var f = new FakeDriver { Muted = true };
        var c = await Connected(f);
        await c.SelectSourceAsync("wifi");
        Assert.Equal(("wifi", true, 1), (f.Source, f.Muted, f.Writes));
    }

    [Fact]
    public async Task MuteResetDuringSwitchFailsClosed()
    {
        var f = new FakeDriver { ResetMuteOnSource = true };
        var c = await Connected(f);
        await c.SelectSourceAsync("wifi");
        Assert.False(c.State.Valid);
        Assert.Equal(2, f.Writes);
    }

    [Fact]
    public async Task SourceSwitchRequiresMuteCapability()
    {
        var f = new FakeDriver { Capabilities = DriverCapabilities.Volume | DriverCapabilities.Source };
        var c = await Connected(f);
        await c.SelectSourceAsync("wifi");
        Assert.Equal(("usb", 0), (f.Source, f.Writes));
    }

    [Fact]
    public async Task AboveCapUnmuteRefused()
    {
        var f = new FakeDriver { Volume = 70, Muted = true };
        var c = await Connected(f);
        await c.ToggleMuteAsync();
        await c.SetMuteAsync(false);
        Assert.Equal(0, f.Writes);
        Assert.True(f.Muted);
    }

    [Fact]
    public async Task AbsoluteMuteIsIdempotent()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        await c.SetMuteAsync(false);
        Assert.Equal(0, f.Writes);
        await c.SetMuteAsync(true);
        Assert.Equal((1, true), (f.Writes, f.Muted));
    }

    [Fact]
    public async Task FailedConnectLeavesNoControl()
    {
        var f = new FakeDriver { Broken = true };
        var c = new SpeakerController { Clock = () => now };
        Assert.False(await c.ConnectAsync(f));
        Assert.False(c.CanControl);
    }

    [Fact]
    public async Task PollBacksOffAfterFailures()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), c.NextPollDelay(true));
        f.Broken = true;
        await c.PollAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), c.NextPollDelay(true));
        await c.PollAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), c.NextPollDelay(true));
        f.Broken = false;
        await c.PollAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(1500), c.NextPollDelay(true));
    }
}
