using KefRemote.Desktop;
using Xunit;

namespace KefRemote.Tests;

public class VolumeKeyAccumulatorTests
{
    static VolumeSnapshot Ready(int volume, int limit = 30, bool muted = false) => new(true, volume, muted, limit);

    [Theory]
    [InlineData(20, 1, 1, 30, 21)]
    [InlineData(29, 1, 2, 30, 30)]   // step clamps to the cap
    [InlineData(30, 1, 1, 30, 30)]   // at cap: no raise
    [InlineData(45, 1, 1, 30, 45)]   // above cap: never raised, never forced down
    [InlineData(45, -1, 1, 30, 44)]  // above cap: lowering still allowed
    [InlineData(1, -1, 2, 30, 0)]    // floor
    [InlineData(0, -1, 1, 30, 0)]
    public void NextTarget_respects_cap_and_floor(int current, int direction, int step, int limit, int expected) =>
        Assert.Equal(expected, VolumeKeyAccumulator.NextTarget(current, direction, step, limit));

    [Fact]
    public void Rapid_presses_send_one_newest_target()
    {
        var acc = new VolumeKeyAccumulator();
        var speaker = Ready(20);
        acc.Press(1, speaker, 0);
        acc.Press(1, speaker, 30);
        acc.Press(1, speaker, 60);
        Assert.Null(acc.TakeDue(100));          // still inside debounce
        Assert.Equal(23, acc.TakeDue(180));     // 60 + 120
        Assert.Null(acc.TakeDue(400));          // one write in flight at a time
    }

    [Fact]
    public void Held_key_sends_intermediate_targets_and_newest_after_each_write()
    {
        var acc = new VolumeKeyAccumulator();
        var speaker = Ready(10);
        long t = 0;
        int? first = null;
        for (; t <= 330 && first == null; t += 33)
        {
            acc.Press(1, speaker, t);
            first = acc.TakeDue(t);
        }
        Assert.NotNull(first);                  // MaxWait forces a send while the key is still held
        Assert.True(t <= 330 + 33);

        // Keep holding while the write is in flight.
        for (int i = 0; i < 5; i++, t += 33) acc.Press(1, speaker, t);
        Assert.Null(acc.TakeDue(t));

        Assert.False(acc.Completed(Ready(first!.Value)));  // newer presses pending: burst continues
        long resumedAt = t;
        int? second = acc.TakeDue(resumedAt + 400);
        Assert.Equal(first + 5, second);
        Assert.True(acc.Completed(Ready(second!.Value)));
        Assert.Null(acc.Target);
    }

    [Fact]
    public void Not_ready_drops_press_without_queueing()
    {
        var acc = new VolumeKeyAccumulator();
        Assert.Equal(PressOutcome.NotReady, acc.Press(1, new VolumeSnapshot(false, 20, false, 30, "offline"), 0));
        Assert.Null(acc.TakeDue(1000));
        Assert.Null(acc.Target);
    }

    [Fact]
    public void Failure_drops_burst_and_next_press_starts_from_verified_volume()
    {
        var acc = new VolumeKeyAccumulator();
        acc.Press(-1, Ready(20), 0);
        acc.Press(-1, Ready(20), 10);
        Assert.Equal(18, acc.TakeDue(500));
        acc.Press(-1, Ready(20), 510);         // arrives during the failing write
        acc.Failed();
        Assert.Null(acc.TakeDue(2000));        // nothing replayed

        acc.Press(-1, Ready(20), 3000);
        Assert.Equal(19, acc.Target);
    }

    [Fact]
    public void Readback_mismatch_ends_burst_and_rebases()
    {
        var acc = new VolumeKeyAccumulator();
        acc.Press(1, Ready(20), 0);
        Assert.Equal(21, acc.TakeDue(500));
        acc.Press(1, Ready(20), 510);
        Assert.True(acc.Completed(Ready(24)));  // someone else changed it: stop, do not chase the old target
        Assert.Null(acc.TakeDue(2000));
        acc.Press(1, Ready(24), 3000);
        Assert.Equal(25, acc.Target);
    }

    [Fact]
    public void Above_cap_up_is_refused_and_down_moves()
    {
        var acc = new VolumeKeyAccumulator();
        Assert.Equal(PressOutcome.AtCap, acc.Press(1, Ready(45), 0));
        Assert.Null(acc.TakeDue(1000));
        Assert.Equal(PressOutcome.Moved, acc.Press(-1, Ready(45), 1000));
        Assert.Equal(44, acc.TakeDue(2000));
    }

    [Fact]
    public void Step_size_is_applied()
    {
        var acc = new VolumeKeyAccumulator { Step = 5 };
        acc.Press(1, Ready(10), 0);
        acc.Press(1, Ready(10), 10);
        Assert.Equal(20, acc.Target);
    }

    [Fact]
    public void Unmute_request_only_after_up_press()
    {
        var acc = new VolumeKeyAccumulator();
        acc.Press(-1, Ready(10, muted: true), 0);
        Assert.False(acc.TakeUnmuteRequest());
        acc.Press(1, Ready(10, muted: true), 0);
        Assert.True(acc.TakeUnmuteRequest());
        Assert.False(acc.TakeUnmuteRequest());
    }
}
