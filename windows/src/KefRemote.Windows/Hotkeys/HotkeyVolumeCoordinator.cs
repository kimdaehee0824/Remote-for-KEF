using System.Diagnostics;

namespace KefRemote.Windows;

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
    readonly VolumeOsdForm osd;
    readonly VolumeKeyAccumulator accumulator = new();
    readonly System.Windows.Forms.Timer sendTimer = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    bool muteInFlight;

    /// <param name="snapshot">Latest verified speaker state from the controller.</param>
    /// <param name="setVolumeTarget">Writes one absolute volume and completes after the controller verified it (e.g. SpeakerController.SetVolumeTarget).</param>
    /// <param name="setMute">Writes mute on/off and completes after verification.</param>
    /// <param name="controllerBusy">True while the controller runs a poll or another command and would drop a mute command.</param>
    public HotkeyVolumeCoordinator(Func<VolumeSnapshot> snapshot, Func<int, Task> setVolumeTarget, Func<bool, Task> setMute,
        Func<bool> controllerBusy, VolumeOsdForm osd)
    {
        this.snapshot = snapshot;
        this.controllerBusy = controllerBusy;
        this.setVolumeTarget = setVolumeTarget;
        this.setMute = setMute;
        this.osd = osd;
        sendTimer.Tick += (_, _) => { sendTimer.Stop(); Pump(); };
        osd.WheelStep += direction => Handle(direction > 0 ? HotkeyAction.VolumeUp : HotkeyAction.VolumeDown);
        osd.MuteClicked += () => Handle(HotkeyAction.ToggleMute);
    }

    public int Step
    {
        get => accumulator.Step;
        set => accumulator.Step = Math.Clamp(value, 1, 10);
    }

    public int Tolerance
    {
        get => accumulator.Tolerance;
        set => accumulator.Tolerance = Math.Max(0, value);
    }

    public bool OsdEnabled { get; set; } = true;

    public void Handle(HotkeyAction action)
    {
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
                Show(state, message: state.Status ?? "스피커 연결을 확인하는 중입니다");
                return;
            case PressOutcome.Moved:
                Show(state, volume: accumulator.Target, pending: true);
                break;
            case PressOutcome.AtCap:
                Show(state, volume: accumulator.Target, pending: accumulator.HasUnsent || accumulator.InFlight,
                    message: (accumulator.Target ?? state.Volume) > state.Limit
                        ? $"현재 음량이 상한 {state.Limit}보다 높습니다"
                        : $"앱 음량 상한 {state.Limit}");
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
        long now = clock.ElapsedMilliseconds;
        var due = accumulator.TakeDue(now);
        if (due is int target)
        {
            _ = Send(target);
            return;
        }
        if (accumulator.DelayUntilDue(now) is long delay)
        {
            sendTimer.Interval = (int)Math.Max(1, delay);
            sendTimer.Start();
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
            // Not applied (cap, input changed elsewhere, ...). The controller's status says why.
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
            Show(state, message: state.Status ?? "스피커 연결을 확인하는 중입니다");
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
            else Show(verified, message: verified.Status ?? "음소거 상태를 확인하지 못했습니다", error: !verified.Ready);
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
        if (!OsdEnabled) return;
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
        sendTimer.Dispose();
    }
}
