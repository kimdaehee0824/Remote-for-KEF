using KefRemote.Core;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Power on/off and approach-from-above volume writes.</summary>
public class SpeakerControllerPowerTests
{
    long now = 10_000;

    async Task<SpeakerController> Connected(FakeDriver f)
    {
        f.Capabilities |= DriverCapabilities.Power;
        var c = new SpeakerController { Clock = () => now, Delay = (_, _) => Task.CompletedTask };
        Assert.True(await c.ConnectAsync(f));
        return c;
    }

    [Fact]
    public async Task PowerOffIsVerified()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        await c.PowerOffAsync();
        Assert.Equal((PowerState.Standby, 1), (f.Power, f.Writes));
        Assert.Equal(SpeakerPhase.Standby, c.Phase);
        Assert.Equal("usb", c.LastActiveSource);
    }

    [Fact]
    public async Task PowerOffWaitsForSlowSpeaker()
    {
        var f = new FakeDriver { PowerLagReads = 5 };
        var c = await Connected(f);
        await c.PowerOffAsync();
        Assert.Equal(PowerState.Standby, c.State.Power);
        Assert.True(c.State.Valid);
    }

    [Fact]
    public async Task PowerOffNeedsCapability()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        f.Capabilities &= ~DriverCapabilities.Power;
        await c.PowerOffAsync();
        Assert.Equal((PowerState.On, 0), (f.Power, f.Writes));
        Assert.NotNull(c.Notice);
    }

    [Fact]
    public async Task PowerOnResumesLastInput()
    {
        var f = new FakeDriver { Power = PowerState.Standby };
        var c = await Connected(f);
        Assert.True(c.CanPowerOn);
        Assert.False(c.CanControl);
        await c.PowerOnAsync();
        Assert.Equal((PowerState.On, "usb", 1), (f.Power, f.Source, f.Writes));
        Assert.Equal(SpeakerPhase.On, c.Phase);
    }

    [Fact]
    public async Task PowerOnShowsTheInputBeforeThePowerStatus()
    {
        var f = new FakeDriver { Power = PowerState.Standby, WakingReadsAfterPowerOn = 3 };
        var c = await Connected(f);
        Assert.Null(c.LastActiveSource);
        var shown = new List<string?>();
        c.Changed += () => { if (c.Operation == SpeakerOperation.PowerOn) shown.Add(c.OperationSource); };
        await c.PowerOnAsync();
        Assert.Contains("usb", shown);
        Assert.Equal((SpeakerPhase.On, "usb", 1), (c.Phase, c.State.SourceId, f.Writes));
        Assert.False(c.IsWaking);
    }

    [Fact]
    public async Task WakingElsewhereShowsTheInputAndPollsSooner()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        await c.PowerOffAsync();
        // Woken with the remote: the input is back, the power status follows a moment later.
        (f.Power, f.WakingReads) = (PowerState.On, 1);
        now += 1000;
        await c.PollAsync();
        Assert.Equal((SpeakerPhase.Standby, "usb"), (c.Phase, c.State.SourceId));
        Assert.True(c.IsWaking);
        Assert.False(c.CanPowerOn);
        Assert.False(c.CanControl);
        Assert.Equal(TimeSpan.FromMilliseconds(500), c.NextPollDelay(false));
        now += 250;
        await c.PollAsync();
        Assert.Equal(SpeakerPhase.On, c.Phase);
        Assert.False(c.IsWaking);
        Assert.Null(c.Notice);
        Assert.Equal(TimeSpan.FromSeconds(4), c.NextPollDelay(false));
    }

    [Fact]
    public async Task PowerOnToOtherInputSwitchesMuted()
    {
        var f = new FakeDriver { Power = PowerState.Standby };
        var c = await Connected(f);
        await c.PowerOnAsync("wifi");
        // power, mute, source, unmute
        Assert.Equal((PowerState.On, "wifi", false, 4), (f.Power, f.Source, f.Muted, f.Writes));
    }

    [Fact]
    public async Task PowerOnRefusedAboveLimit()
    {
        var f = new FakeDriver { Power = PowerState.Standby, Volume = 45 };
        var c = await Connected(f);
        await c.PowerOnAsync();
        Assert.Equal((PowerState.Standby, 0), (f.Power, f.Writes));
        Assert.Contains("상한", c.Notice);
    }

    [Fact]
    public async Task PowerOnIgnoredWhenAlreadyOn()
    {
        var f = new FakeDriver();
        var c = await Connected(f);
        await c.PowerOnAsync();
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task PowerOnWaitsForASlowWake()
    {
        // Past the 32 reads the old wait of about 10 s allowed.
        var f = new FakeDriver { Power = PowerState.Standby, PowerLagReads = 34 };
        var c = await Connected(f);
        await c.PowerOnAsync();
        Assert.Equal((SpeakerPhase.On, "usb", 1), (c.Phase, c.State.SourceId, f.Writes));
        Assert.Null(c.Notice);
    }

    [Fact]
    public async Task PowerOnReadsAtMostTwiceASecond()
    {
        var f = new FakeDriver { Power = PowerState.Standby, IgnoreWrites = true };
        var delays = new List<int>();
        var c = new SpeakerController { Clock = () => now, Delay = (ms, _) => { delays.Add(ms); return Task.CompletedTask; } };
        f.Capabilities |= DriverCapabilities.Power;
        Assert.True(await c.ConnectAsync(f));
        await c.PowerOnAsync();
        Assert.All(delays, ms => Assert.True(ms >= 500));
        Assert.InRange(delays.Sum(), 28_000, 32_000);
    }

    [Fact]
    public async Task StillWakingAfterTheWaitIsNotAFailure()
    {
        var f = new FakeDriver { Power = PowerState.Standby, WakingReadsAfterPowerOn = 1000 };
        var c = await Connected(f);
        await c.PowerOnAsync();
        Assert.Equal(1, f.Writes);
        Assert.True(c.State.Valid);
        Assert.True(c.IsWaking);
        Assert.Equal(SpeakerPhase.Standby, c.Phase);
        Assert.NotNull(c.Notice);
        Assert.False(c.NoticeIsError);
        Assert.Equal(TimeSpan.FromMilliseconds(500), c.NextPollDelay(false));
        // The speaker gets there in the end: shown as on, and the notice goes.
        f.WakingReads = 0;
        now += 500;
        await c.PollAsync();
        Assert.Equal(SpeakerPhase.On, c.Phase);
        Assert.Null(c.Notice);
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task UnconfirmedPowerOnIsReportedAndClearedOnceOn()
    {
        var f = new FakeDriver { Power = PowerState.Standby, IgnoreWrites = true };
        var c = await Connected(f);
        await c.PowerOnAsync();
        Assert.Equal(1, f.Writes);
        // The reads worked: still connected, in standby, nothing else written.
        Assert.True(c.State.Valid);
        Assert.Equal(SpeakerPhase.Standby, c.Phase);
        Assert.False(c.CanControl);
        Assert.True(c.NoticeIsError);
        // Turned on later, with the remote or by the speaker itself.
        f.Power = PowerState.On;
        now += 4000;
        await c.PollAsync();
        Assert.Equal(SpeakerPhase.On, c.Phase);
        Assert.Null(c.Notice);
    }

    [Fact]
    public async Task LateWakeSaysTheInputWasNotSwitched()
    {
        var f = new FakeDriver { Power = PowerState.Standby, WakingReadsAfterPowerOn = 1000 };
        var c = await Connected(f);
        await c.PowerOnAsync("wifi");
        Assert.Equal(1, f.Writes);
        f.WakingReads = 0;
        now += 500;
        await c.PollAsync();
        Assert.Equal((SpeakerPhase.On, "usb", 1), (c.Phase, c.State.SourceId, f.Writes));
        Assert.NotNull(c.Notice);
        Assert.False(c.NoticeIsError);
    }

    [Fact]
    public async Task NewerNoticeIsKeptWhenTheLateWakeEnds()
    {
        var f = new FakeDriver { Power = PowerState.Standby, WakingReadsAfterPowerOn = 1000 };
        var c = await Connected(f);
        await c.PowerOnAsync();
        c.ClearNotice();
        f.WakingReads = 0;
        now += 500;
        await c.PollAsync();
        Assert.Equal(SpeakerPhase.On, c.Phase);
        Assert.Null(c.Notice);
    }

    [Fact]
    public async Task ApproachFromAboveWritesOvershootThenTarget()
    {
        var f = new FakeDriver { Volume = 12, Overshoot = (_, t) => t + 2 };
        var c = await Connected(f);
        c.ApproachFromAbove = true;
        await c.SetVolumeTargetAsync(20);
        Assert.Equal([22, 20], f.VolumeWrites);
        Assert.Equal(20, c.State.Volume);
    }

    [Fact]
    public async Task ApproachOvershootIsCappedAtLimit()
    {
        var f = new FakeDriver { Volume = 12, Overshoot = (_, t) => t + 5 };
        var c = await Connected(f);
        c.ApproachFromAbove = true;
        c.AppVolumeLimit = 22;
        await c.SetVolumeTargetAsync(20);
        Assert.Equal([22, 20], f.VolumeWrites);
        await c.SetVolumeTargetAsync(22);
        // Overshoot would be 27 but the limit is 22, which is the target itself: write directly.
        Assert.Equal([22, 20, 22], f.VolumeWrites);
    }

    [Fact]
    public async Task ApproachOnlyWhenRaisingAndEnabled()
    {
        var f = new FakeDriver { Volume = 25, Overshoot = (_, t) => t + 2 };
        var c = await Connected(f);
        await c.SetVolumeTargetAsync(28);
        c.ApproachFromAbove = true;
        await c.SetVolumeTargetAsync(20);
        Assert.Equal([28, 20], f.VolumeWrites);
    }

    [Fact]
    public async Task ApproachStopsWhenInvalidatedBetweenWrites()
    {
        var f = new FakeDriver { Volume = 12, Overshoot = (_, t) => t + 2 };
        var c = new SpeakerController { Clock = () => now };
        Assert.True(await c.ConnectAsync(f));
        c.ApproachFromAbove = true;
        var hold = new TaskCompletionSource();
        c.Delay = (_, _) => hold.Task;
        var task = c.SetVolumeTargetAsync(20);
        c.Invalidate("stop");
        hold.SetResult();
        await task;
        Assert.Equal([22], f.VolumeWrites);
    }

    [Theory]
    [InlineData(12, 17)]
    [InlineData(20, 22)]
    [InlineData(23, 25)]
    [InlineData(30, 31)]
    [InlineData(40, 41)]
    public void KefOvershootTable(int target, int expected) =>
        Assert.Equal(expected, KefRemote.Drivers.Kef.KefVolumeCurve.Overshoot(target));

    [Theory]
    [InlineData(5)]
    [InlineData(41)]
    [InlineData(80)]
    public void KefOvershootNotNeeded(int target) =>
        Assert.Null(KefRemote.Drivers.Kef.KefVolumeCurve.Overshoot(target));
}
