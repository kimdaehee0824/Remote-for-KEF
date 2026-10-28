namespace KefRemote.Windows;

/// <summary>
/// What the hotkey layer needs to know about the speaker. <see cref="Ready"/> is true when the controller holds a
/// fresh, verified state of a powered-on speaker, ignoring whether a command or poll is running right now.
/// <see cref="Status"/> is the controller's latest human-readable status, shown when a press could not be applied.
/// </summary>
public readonly record struct VolumeSnapshot(bool Ready, int Volume, bool Muted, int Limit, string? Status = null);

public enum PressOutcome
{
    /// <summary>Target moved; preview it.</summary>
    Moved,
    /// <summary>Volume up hit the effective cap (or was already above it); nothing will be sent.</summary>
    AtCap,
    /// <summary>Volume down at 0.</summary>
    AtFloor,
    /// <summary>The speaker is not ready; the press is dropped, never queued.</summary>
    NotReady,
}

/// <summary>
/// Folds rapid or held volume presses into one target and decides when to send it.
/// Pure logic with an injected clock (milliseconds) so it can be tested without timers or a speaker.
///
/// Rules, following the handoff's safety policy:
///  - The first press of a burst starts from the last verified volume, never from a stale preview.
///  - Up never goes past the effective cap; a volume already above the cap is not raised but may be lowered.
///  - Only one write is in flight. Presses during it keep moving the target, and only the newest target is sent next.
///  - A failed write drops the burst. Nothing is replayed later.
/// </summary>
public sealed class VolumeKeyAccumulator
{
    public int Step { get; set; } = 1;
    /// <summary>Quiet time after the last press before the target is sent.</summary>
    public long DebounceMs { get; set; } = 120;
    /// <summary>Upper bound on how long a held key waits before an intermediate target is sent.</summary>
    public long MaxWaitMs { get; set; } = 300;
    /// <summary>Read-back difference the driver still counts as applied (ISpeakerDriver.VolumeTolerance).</summary>
    public int Tolerance { get; set; }

    int? target;
    int? sentTarget;
    long burstStartedAt;
    long lastPressAt;
    bool pressedUpInBurst;

    public bool InFlight { get; private set; }
    public int? Target => target;
    public bool HasUnsent => target != null && target != sentTarget;

    public PressOutcome Press(int direction, VolumeSnapshot speaker, long now)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!speaker.Ready)
        {
            // Keep an in-flight burst alive; it will be judged against the read-back result.
            if (!InFlight)
            {
                pressedUpInBurst = false;
                Reset();
            }
            return PressOutcome.NotReady;
        }

        int current = target ?? speaker.Volume;
        int next = NextTarget(current, direction, Step, speaker.Limit);
        if (direction > 0) pressedUpInBurst = true;
        if (next == current)
            return direction > 0 ? PressOutcome.AtCap : PressOutcome.AtFloor;

        if (!HasUnsent) burstStartedAt = now;
        target = next;
        lastPressAt = now;
        return PressOutcome.Moved;
    }

    /// <summary>Returns the target to send now, or null. The caller must call <see cref="Completed"/> or <see cref="Failed"/> afterwards.</summary>
    public int? TakeDue(long now)
    {
        if (InFlight || !HasUnsent) return null;
        if (now - lastPressAt < DebounceMs && now - burstStartedAt < MaxWaitMs) return null;
        InFlight = true;
        sentTarget = target;
        return target;
    }

    /// <summary>Milliseconds until <see cref="TakeDue"/> could return a value, or null when nothing is waiting.</summary>
    public long? DelayUntilDue(long now)
    {
        if (InFlight || !HasUnsent) return null;
        long byDebounce = lastPressAt + DebounceMs - now;
        long byMaxWait = burstStartedAt + MaxWaitMs - now;
        return Math.Max(0, Math.Min(byDebounce, byMaxWait));
    }

    /// <summary>
    /// The write finished and the controller read the speaker back.
    /// Returns true when the burst is over (no newer presses) and the caller should show the verified value.
    /// </summary>
    public bool Completed(VolumeSnapshot verified)
    {
        InFlight = false;
        if (!verified.Ready || sentTarget is not int sent || Math.Abs(verified.Volume - sent) > Tolerance)
        {
            Reset();
            return true;
        }
        if (target == sentTarget)
        {
            Reset();
            return true;
        }
        burstStartedAt = lastPressAt;
        return false;
    }

    public void Failed()
    {
        InFlight = false;
        pressedUpInBurst = false;
        Reset();
    }

    /// <summary>True once per finished burst that contained a volume-up press, mirroring how Windows unmutes on volume up.</summary>
    public bool TakeUnmuteRequest()
    {
        bool value = pressedUpInBurst;
        pressedUpInBurst = false;
        return value;
    }

    void Reset()
    {
        target = null;
        sentTarget = null;
        // pressedUpInBurst survives until TakeUnmuteRequest so the caller can act on a completed burst.
    }

    public static int NextTarget(int current, int direction, int step, int limit)
    {
        if (current is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(current));
        if (step is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(step));
        limit = Math.Clamp(limit, 0, 100);
        if (direction > 0) return current >= limit ? current : Math.Min(limit, current + step);
        return Math.Max(0, current - step);
    }
}
