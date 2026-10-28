using System.Diagnostics;

namespace KefRemote.Desktop;

/// <param name="Volume">Level to draw, or null when the speaker state is unknown.</param>
/// <param name="Pending">True while the level is a preview that the speaker has not confirmed yet.</param>
public sealed record OsdContent(int? Volume, bool Muted, int Limit, bool Pending, string? Message, bool IsError);

/// <summary>The on-screen volume display. Implemented by each front end.</summary>
public interface IVolumeOsd
{
    void ShowLevel(OsdContent content);
}

/// <summary>
/// Turns hotkey (and OSD wheel/click) input into speaker writes and keeps the OSD in step.
/// Lives on the UI thread. The controller stays the only place that talks to the speaker:
/// this class hands it one target volume at a time and then reads back what the controller verified.
/// </summary>
public sealed class HotkeyVolumeCoordinator : IDisposable
{
    const int BusyRetryMs = 40, BusyGiveUpMs = 3000;

    readonly Func<VolumeSnapshot> snapshot;
    readonly Func<bool> controllerBusy;
    readonly Func<int, Task> setVolumeTarget;
    readonly Func<bool, Task> setMute;
    readonly IVolumeOsd osd;
    readonly Action<int, Action> runLater;
    readonly VolumeKeyAccumulator accumulator = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    int timerGeneration;
    bool muteInFlight;
    bool disposed;

    /// <param name="snapshot">Latest verified speaker state from the controller.</param>
    /// <param name="setVolumeTarget">Writes one absolute volume and completes after the controller verified it (e.g. SpeakerController.SetVolumeTargetAsync).</param>
    /// <param name="setMute">Writes mute on/off and completes after verification.</param>
    /// <param name="controllerBusy">True while the controller runs a poll or another command and would drop a mute command.</param>
    /// <param name="runLater">Runs the callback on the UI thread after the given milliseconds.</param>
    public HotkeyVolumeCoordinator(Func<VolumeSnapshot> snapshot, Func<int, Task> setVolumeTarget, Func<bool, Task> setMute,
        Func<bool> controllerBusy, IVolumeOsd osd, Action<int, Action> runLater)
    {
        this.snapshot = snapshot;
        this.controllerBusy = controllerBusy;
        this.setVolumeTarget = setVolumeTarget;
        this.setMute = setMute;
        this.osd = osd;
        this.runLater = runLater;
    }

    /// <summary>1–10 volume units per press, or <see cref="VolumeSteps.Perceptual"/>.</summary>
    public int Step
    {
        get => accumulator.Step;
        set => accumulator.Step = value == VolumeSteps.Perceptual ? value : Math.Clamp(value, 1, 10);
    }

    public int Tolerance
    {
        get => accumulator.Tolerance;
        set => accumulator.Tolerance = Math.Max(0, value);
    }

    /// <summary>When false, only problems are shown, not every change.</summary>
    public bool OsdEnabled { get; set; } = true;

    public void Handle(HotkeyAction action)
    {
        if (disposed) return;
        switch (action)
        {
            case HotkeyAction.VolumeUp: Nudge(+1); break;
            case HotkeyAction.VolumeDown: Nudge(-1); break;
            case HotkeyAction.ToggleMute: _ = ToggleMute(); break;
        }
    }

    void Nudge(int direction)
    {
        var state = snapshot();
        var outcome = accumulator.Press(direction, state, clock.ElapsedMilliseconds);
        switch (outcome)
        {
            case PressOutcome.NotReady:
                Show(state, message: state.Status ?? "스피커 연결을 확인하는 중입니다", error: true);
                return;
            case PressOutcome.Moved:
                Show(state, volume: accumulator.Target, pending: true);
                break;
            case PressOutcome.AtCap:
                Show(state, volume: accumulator.Target, pending: accumulator.HasUnsent || accumulator.InFlight,
                    message: (accumulator.Target ?? state.Volume) > state.Limit
                        ? $"현재 음량이 상한 {state.Limit}보다 높습니다"
                        : $"상한 {state.Limit}에 닿았습니다");
                break;
            case PressOutcome.AtFloor:
                Show(state, volume: accumulator.Target, pending: accumulator.HasUnsent || accumulator.InFlight);
                break;
        }
        // Volume up while muted also unmutes, like the Windows volume keys. With nothing to write first, do it now.
        if (direction > 0 && state.Muted && !accumulator.InFlight && !accumulator.HasUnsent && accumulator.TakeUnmuteRequest())
        {
            _ = Unmute();
            return;
        }
        Pump();
    }

    void Pump()
    {
        if (disposed) return;
        long now = clock.ElapsedMilliseconds;
        var due = accumulator.TakeDue(now);
        if (due is int target)
        {
            _ = Send(target);
            return;
        }
        if (accumulator.DelayUntilDue(now) is long delay)
        {
            int scheduled = ++timerGeneration;
            runLater((int)Math.Max(1, delay), () =>
            {
                if (scheduled == timerGeneration) Pump();
            });
        }
    }

    async Task Send(int target)
    {
        try
        {
            await setVolumeTarget(target);
        }
        catch (Exception e)
        {
            accumulator.Failed();
            Show(snapshot(), message: "음량을 바꾸지 못했습니다: " + e.Message, error: true);
            return;
        }

        var verified = snapshot();
        if (!accumulator.Completed(verified))
        {
            // Newer presses arrived while writing. Keep previewing them and send the newest.
            Show(verified, volume: accumulator.Target, pending: true);
            Pump();
            return;
        }

        if (!verified.Ready)
        {
            Show(verified, message: verified.Status ?? "스피커 반영을 확인하지 못했습니다", error: true);
            return;
        }
        if (Math.Abs(verified.Volume - target) > Tolerance)
        {
            // Not applied (cap, input changed elsewhere, ...). The controller's notice says why.
            Show(verified, message: verified.Status ?? $"스피커가 {verified.Volume}(으)로 응답했습니다");
            return;
        }
        Show(verified);
        if (accumulator.TakeUnmuteRequest() && verified.Muted) await Unmute();
    }

    async Task ToggleMute()
    {
        var state = snapshot();
        if (!state.Ready)
        {
            Show(state, message: state.Status ?? "스피커 연결을 확인하는 중입니다", error: true);
            return;
        }
        if (muteInFlight || accumulator.InFlight) return;
        await WriteMute(!state.Muted);
    }

    Task Unmute() => muteInFlight ? Task.CompletedTask : WriteMute(false);

    async Task WriteMute(bool muted)
    {
        muteInFlight = true;
        var before = snapshot();
        Show(before with { Muted = muted }, pending: true);
        try
        {
            // A background poll makes the controller drop commands; wait for it instead of losing the press.
            for (int waited = 0; controllerBusy(); waited += BusyRetryMs)
            {
                if (waited > BusyGiveUpMs)
                {
                    Show(snapshot(), message: "스피커가 응답하지 않아 음소거를 바꾸지 않았습니다", error: true);
                    return;
                }
                await Task.Delay(BusyRetryMs);
            }
            await setMute(muted);
            var verified = snapshot();
            if (verified.Ready && verified.Muted == muted) Show(verified);
            else Show(verified, message: verified.Status ?? "음소거 상태를 확인하지 못했습니다", error: true);
        }
        catch (Exception e)
        {
            Show(snapshot(), message: (muted ? "음소거" : "음소거 해제") + "하지 못했습니다: " + e.Message, error: true);
        }
        finally
        {
            muteInFlight = false;
        }
    }

    void Show(VolumeSnapshot state, int? volume = null, bool pending = false, string? message = null, bool error = false)
    {
        if (disposed || (!OsdEnabled && !error)) return;
        osd.ShowLevel(new OsdContent(
            Volume: state.Ready || volume != null ? volume ?? state.Volume : null,
            Muted: state.Muted,
            Limit: state.Limit,
            Pending: pending,
            Message: message,
            IsError: error));
    }

    public void Dispose()
    {
        disposed = true;
        timerGeneration++;
    }
}
