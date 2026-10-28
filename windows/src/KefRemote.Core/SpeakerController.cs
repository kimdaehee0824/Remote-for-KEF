namespace KefRemote.Core;

/// <summary>Coarse state for the UI. Derived from the controller, never set directly.</summary>
public enum SpeakerPhase
{
    /// <summary>No driver. The app has nothing to connect to.</summary>
    Disconnected,
    /// <summary>Identifying the device or waiting for the first good read.</summary>
    Connecting,
    /// <summary>The last connect or read failed. Polling keeps retrying read-only.</summary>
    Offline,
    Standby,
    On,
}

/// <summary>The user command that is running, so the UI can show progress on the control that started it.</summary>
public enum SpeakerOperation
{
    None,
    Volume,
    Mute,
    Source,
    PowerOn,
    PowerOff,
}

/// <summary>
/// Owns the connection to one device and applies the safety policy to every user command.
///
/// Rules (see docs/drivers.md):
/// - Connecting and polling only read. Nothing is written without a user action.
/// - One request at a time. Mute, input and power commands that arrive while busy are dropped.
///   Volume input that arrives while busy is merged into a single latest target, applied once the
///   current request finishes and only if nothing failed or disconnected in between.
/// - Every command re-reads the device first and aborts if the input changed since the user saw it.
///   This read, not the age of the last poll, is what makes a command safe, so a command is accepted
///   whenever the last poll succeeded within <see cref="StaleMilliseconds"/>.
/// - Every write is confirmed by reading back. A mutation is never retried.
/// - Any failure invalidates the state, which blocks commands until the next successful poll.
/// - Disconnecting or invalidating bumps a generation counter so in-flight work is discarded.
///
/// Not thread safe: call from a single (UI) synchronization context.
/// </summary>
public sealed class SpeakerController : IDisposable
{
    /// <summary>
    /// A state older than this is treated as unknown. Well above the slowest poll interval plus the request timeout,
    /// so a healthy connection never drops in and out between polls.
    /// </summary>
    public const int StaleMilliseconds = 15000;

    /// <summary>Pause between the overshoot and the target when approaching a volume from above.</summary>
    public const int ApproachHoldMilliseconds = 100;

    const int VerifyAttempts = 4, VerifyDelayMs = 180;
    const int PowerVerifyAttempts = 16, PowerVerifyDelayMs = 500;
    static readonly int[] Backoff = [2000, 5000, 10000, 30000];

    int generation;
    int failures;
    int appVolumeLimit = 30;
    CancellationTokenSource cts = new();
    PendingVolume? pendingVolume;

    sealed class PendingVolume
    {
        public int? Target;
        public int Delta;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ISpeakerDriver? Driver { get; private set; }
    public DeviceInfo? Device { get; private set; }
    public SpeakerState State { get; private set; } = SpeakerState.Unknown;
    public bool Busy { get; private set; }

    /// <summary>Connection and device state in words, e.g. for a tooltip. Results of commands go to <see cref="Notice"/>.</summary>
    public string Status { get; private set; } = "연결 전";

    /// <summary>Outcome of the last user command when it needs explaining (refused, capped, kept muted, failed).</summary>
    public string? Notice { get; private set; }
    public bool NoticeIsError { get; private set; }

    /// <summary><see cref="Clock"/> time the notice was set, so the UI can let it expire.</summary>
    public long NoticeAt { get; private set; }

    public SpeakerOperation Operation { get; private set; }

    /// <summary>Input a running source switch or power-on is heading for.</summary>
    public string? OperationSource { get; private set; }

    /// <summary>The input last seen while the speaker was on. Power-on returns to it.</summary>
    public string? LastActiveSource { get; private set; }

    /// <summary>
    /// Raise the volume by briefly writing a higher value first when the driver says the device needs it
    /// (<see cref="ISpeakerDriver.ApproachOvershoot"/>). The overshoot never exceeds <see cref="EffectiveLimit"/>.
    /// </summary>
    public bool ApproachFromAbove { get; set; }

    /// <summary>Raised after any change of state, status or busy flag.</summary>
    public event Action? Changed;

    /// <summary>Monotonic milliseconds. Replaceable for tests.</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Delay between readback attempts. Replaceable for tests.</summary>
    public Func<int, CancellationToken, Task> Delay { get; set; } = (ms, ct) => Task.Delay(ms, ct);

    /// <summary>Upper bound for volume raises made by this app. Lowering is always allowed.</summary>
    public int AppVolumeLimit
    {
        get => appVolumeLimit;
        set { appVolumeLimit = Math.Clamp(value, 1, 100); Notify(); }
    }

    public int EffectiveLimit => Math.Min(appVolumeLimit, Device?.DeviceVolumeLimit ?? 100);

    /// <summary>True when the speaker's own maximum volume, not the app limit, sets <see cref="EffectiveLimit"/>.</summary>
    public bool LimitFromDevice => Device?.DeviceVolumeLimit is int d && d < appVolumeLimit;

    public bool Fresh
    {
        get
        {
            long now = Clock();
            return State.Valid && now >= State.SeenAt && now - State.SeenAt < StaleMilliseconds;
        }
    }

    /// <summary>Identified and holding a recent verified state, powered on or not.</summary>
    public bool IsConnected => Driver != null && Device != null && Fresh;

    /// <summary>Connected and powered on. Ignores <see cref="Busy"/> so callers can hold input while a write runs.</summary>
    public bool IsReady => IsConnected && State.Active;

    public bool IsStandby => IsConnected && State.Power == PowerState.Standby;

    public bool CanControl => IsReady && !Busy;

    public bool CanPowerOn => IsStandby && !Busy && Supports(DriverCapabilities.Power);

    public SpeakerPhase Phase
    {
        get
        {
            if (Driver == null) return SpeakerPhase.Disconnected;
            if (IsConnected) return State.Active ? SpeakerPhase.On : SpeakerPhase.Standby;
            return failures > 0 ? SpeakerPhase.Offline : SpeakerPhase.Connecting;
        }
    }

    public bool Supports(DriverCapabilities capability) => Driver != null && Driver.Capabilities.HasFlag(capability);

    /// <summary>Replace the driver, identify the device and read once. Never writes.</summary>
    public async Task<bool> ConnectAsync(ISpeakerDriver driver)
    {
        Disconnect("연결 확인 중");
        Driver = driver;
        int token = generation;
        Busy = true;
        Notify();
        try
        {
            var device = await driver.ConnectAsync(cts.Token);
            if (token != generation) return false;
            Device = device;
            await ReadAsync(token);
            failures = 0;
            Status = State.Active ? "연결됨" : "스피커가 대기 상태입니다";
            return true;
        }
        catch (Exception e) when (token == generation)
        {
            failures++;
            Fail("연결 실패: " + Describe(e));
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token == generation) Busy = false;
            Notify();
        }
    }

    /// <summary>
    /// Re-identify the device read-only, e.g. to pick up a maximum volume changed in the vendor app.
    /// Skipped while a command runs.
    /// </summary>
    public async Task RefreshDeviceAsync()
    {
        if (Busy || Driver == null || Device == null) return;
        int token = generation;
        Busy = true;
        try
        {
            var device = await Driver.ConnectAsync(cts.Token);
            if (token == generation) Device = device;
        }
        catch
        {
            // The regular poll reports connection problems.
        }
        finally
        {
            if (token == generation) Busy = false;
            Notify();
            if (token == generation) DrainPendingVolume();
        }
    }

    /// <summary>Drop the driver and discard any in-flight work.</summary>
    public void Disconnect(string reason)
    {
        NextGeneration();
        Driver?.Dispose();
        Driver = null;
        Device = null;
        State = SpeakerState.Unknown;
        Busy = false;
        failures = 0;
        Operation = SpeakerOperation.None;
        OperationSource = null;
        Status = reason;
        Notify();
    }

    /// <summary>Discard in-flight work and block commands until the next successful poll.</summary>
    public void Invalidate(string reason)
    {
        NextGeneration();
        State = State with { Valid = false };
        Busy = false;
        Operation = SpeakerOperation.None;
        OperationSource = null;
        Status = reason;
        Notify();
    }

    public void ClearNotice()
    {
        if (Notice == null) return;
        Notice = null;
        NoticeIsError = false;
        Notify();
    }

    public async Task PollAsync()
    {
        if (Busy || Driver == null || Device == null) return;
        int token = generation;
        Busy = true;
        try
        {
            var previous = State;
            await ReadAsync(token);
            failures = 0;
            if (previous.Valid && previous.Active && State.Active && previous.SourceId != State.SourceId)
                SetNotice("다른 곳에서 입력을 바꿨습니다");
            Status = State.Active ? "연결됨" : "스피커가 대기 상태입니다";
        }
        catch (Exception e) when (token == generation)
        {
            failures++;
            Fail("연결 확인 필요: " + Describe(e));
        }
        catch
        {
        }
        finally
        {
            if (token == generation) Busy = false;
            Notify();
            if (token == generation) DrainPendingVolume();
        }
    }

    /// <summary>Suggested delay before the next poll, with backoff after failures.</summary>
    public TimeSpan NextPollDelay(bool windowVisible) =>
        TimeSpan.FromMilliseconds(failures == 0
            ? windowVisible ? 1500 : 4000
            : Backoff[Math.Min(failures, Backoff.Length) - 1]);

    /// <summary>Change the volume relative to the value read right before writing.</summary>
    public Task StepVolumeAsync(int delta) => VolumeCommand(null, delta);

    /// <summary>
    /// Set an absolute volume. The task completes after the read-back. While busy, the latest
    /// target replaces earlier ones and the task completes when that merged target is done.
    /// </summary>
    public Task SetVolumeTargetAsync(int target) => VolumeCommand(target, 0);

    Task VolumeCommand(int? target, int delta)
    {
        if (Busy && IsReady)
        {
            var p = pendingVolume ??= new PendingVolume();
            if (target is int t) (p.Target, p.Delta) = (t, 0);
            else if (p.Target is int pt) p.Target = pt + delta;
            else p.Delta += delta;
            return p.Done.Task;
        }
        return Run(SpeakerOperation.Volume, null, (before, token) => WriteVolume(before, target ?? before.Volume + delta, token));
    }

    void DrainPendingVolume()
    {
        var p = pendingVolume;
        if (p == null || Busy) return;
        pendingVolume = null;
        if (!CanControl)
        {
            p.Done.TrySetResult();
            return;
        }
        _ = CompleteWhenDone(p, Run(SpeakerOperation.Volume, null,
            (before, token) => WriteVolume(before, p.Target ?? before.Volume + p.Delta, token)));
    }

    static async Task CompleteWhenDone(PendingVolume p, Task task)
    {
        try { await task; }
        finally { p.Done.TrySetResult(); }
    }

    public Task ToggleMuteAsync() => Run(SpeakerOperation.Mute, null, (before, token) => WriteMute(before, !before.Muted, token));

    public Task SetMuteAsync(bool muted) => Run(SpeakerOperation.Mute, null, (before, token) => WriteMute(before, muted, token));

    async Task WriteMute(SpeakerState before, bool muted, int token)
    {
        if (!Supports(DriverCapabilities.Mute) || before.Muted == muted) return;
        if (!muted && before.Volume > EffectiveLimit)
        {
            SetNotice($"음량({before.Volume})이 상한({EffectiveLimit})보다 높아 음소거를 해제하지 않았습니다. 먼저 음량을 낮추세요");
            return;
        }
        await Driver!.SetMuteAsync(muted, cts.Token);
        await Verify(token, s => s.SourceId == before.SourceId && s.Muted == muted);
    }

    public Task SelectSourceAsync(string sourceId) => Run(SpeakerOperation.Source, sourceId, async (before, token) =>
    {
        if (!Supports(DriverCapabilities.Source)) return;
        if (!Driver!.Sources.Any(s => s.Id == sourceId)) throw new SpeakerException("지원하지 않는 입력: " + sourceId);
        await SwitchSource(before, sourceId, token);
    });

    /// <summary>
    /// Each input can have its own volume, so switching is only done muted:
    /// mute, verify, switch, verify, then unmute only if the new input is not louder and within the limit.
    /// </summary>
    async Task SwitchSource(SpeakerState before, string sourceId, int token)
    {
        if (before.SourceId == sourceId) return;
        if (!Supports(DriverCapabilities.Mute)) throw new SpeakerException("음소거를 지원하지 않는 기기는 안전하게 입력을 전환할 수 없습니다");

        if (!before.Muted)
        {
            await Driver!.SetMuteAsync(true, cts.Token);
            await Verify(token, s => s.Muted && s.SourceId == before.SourceId);
        }
        Check(token);
        await Driver!.SetSourceAsync(sourceId, cts.Token);
        await Verify(token, s => s.SourceId == sourceId && s.Muted);

        var after = State;
        if (before.Muted)
        {
            SetNotice("입력을 바꿨습니다. 원래 음소거 상태를 유지합니다");
        }
        else if (after.Volume <= before.Volume && after.Volume <= EffectiveLimit)
        {
            Check(token);
            await Driver.SetMuteAsync(false, cts.Token);
            await Verify(token, s => s.SourceId == sourceId && !s.Muted);
        }
        else
        {
            SetNotice($"새 입력의 음량({after.Volume})이 이전보다 높거나 상한을 넘어 음소거를 유지했습니다");
        }
    }

    /// <summary>Put the speaker in standby. Verified by reading the power state back.</summary>
    public Task PowerOffAsync() => Run(SpeakerOperation.PowerOff, null, async (before, token) =>
    {
        if (!Supports(DriverCapabilities.Power))
        {
            SetNotice("이 기기는 앱에서 전원을 끌 수 없습니다");
            return;
        }
        await Driver!.SetPowerAsync(false, cts.Token);
        await Verify(token, s => s.Power == PowerState.Standby, PowerVerifyAttempts, PowerVerifyDelayMs, tolerateTransient: true);
        Status = "스피커가 대기 상태입니다";
    });

    /// <summary>
    /// Wake from standby, then switch to <paramref name="sourceId"/> with the usual muted switch when it differs
    /// from the input the speaker woke up on. Refused when the volume read in standby is above the limit.
    /// </summary>
    public async Task PowerOnAsync(string? sourceId = null)
    {
        if (!CanPowerOn) return;
        int token = generation;
        Busy = true;
        Operation = SpeakerOperation.PowerOn;
        OperationSource = sourceId ?? LastActiveSource;
        Notice = null;
        Notify();
        try
        {
            var before = await ReadAsync(token);
            if (sourceId != null && !Driver!.Sources.Any(s => s.Id == sourceId)) throw new SpeakerException("지원하지 않는 입력: " + sourceId);
            if (!before.Active)
            {
                if (before.Volume > EffectiveLimit)
                {
                    SetNotice($"대기 전 음량({before.Volume})이 상한({EffectiveLimit})보다 높아 켜지 않았습니다. 리모컨이나 KEF Connect로 음량을 낮춘 뒤 켜세요");
                    return;
                }
                await Driver!.SetPowerAsync(true, cts.Token);
                await Verify(token, s => s.Active && s.SourceId != null, PowerVerifyAttempts, PowerVerifyDelayMs, tolerateTransient: true);
                Status = "연결됨";
            }
            var on = State;
            if (on.Volume > EffectiveLimit)
            {
                SetNotice($"켜졌습니다. 음량({on.Volume})이 상한({EffectiveLimit})보다 높으니 먼저 낮추세요");
                return;
            }
            if (sourceId != null && Supports(DriverCapabilities.Source))
            {
                Check(token);
                OperationSource = sourceId;
                await SwitchSource(on, sourceId, token);
            }
        }
        catch (Exception e) when (token == generation)
        {
            failures = Math.Max(failures, 1);
            Fail("조작 중지 (자동 재시도 없음): " + Describe(e));
            SetNotice("스피커를 켜지 못했습니다: " + Describe(e), error: true);
        }
        catch
        {
        }
        finally
        {
            if (token == generation)
            {
                Busy = false;
                Operation = SpeakerOperation.None;
                OperationSource = null;
            }
            Notify();
        }
    }

    /// <summary>Volume the app will actually request for <paramref name="requested"/>.</summary>
    public static int ClampTarget(int current, int requested, int limit)
    {
        int target = Math.Clamp(requested, 0, 100);
        if (target <= current) return target;
        return current >= limit ? current : Math.Min(target, limit);
    }

    async Task WriteVolume(SpeakerState before, int requested, int token)
    {
        if (!Supports(DriverCapabilities.Volume)) return;
        int limit = EffectiveLimit;
        int target = ClampTarget(before.Volume, requested, limit);
        if (target == before.Volume)
        {
            if (requested > before.Volume)
                SetNotice(before.Volume > limit ? $"음량이 상한 {limit}보다 높아 올리지 않았습니다" : $"상한 {limit}에 닿았습니다");
            return;
        }
        if (target == limit && requested > target) SetNotice($"상한 {limit}에 닿았습니다");

        var driver = Driver!;
        if (ApproachFromAbove && target > before.Volume && driver.ApproachOvershoot(before.Volume, target) is int over)
        {
            // The overshoot is a passing value: capped at the limit, never verified on its own.
            int passing = Math.Min(over, limit);
            if (passing > target)
            {
                await driver.SetVolumeAsync(passing, cts.Token);
                await Delay(ApproachHoldMilliseconds, cts.Token);
                Check(token);
            }
        }
        int tolerance = driver.VolumeTolerance;
        await driver.SetVolumeAsync(target, cts.Token);
        await Verify(token, s => s.SourceId == before.SourceId && Math.Abs(s.Volume - target) <= tolerance);
    }

    async Task Run(SpeakerOperation operation, string? operationSource, Func<SpeakerState, int, Task> body)
    {
        if (!CanControl) return;
        int token = generation;
        var observed = State;
        Busy = true;
        Operation = operation;
        OperationSource = operationSource;
        if (operation != SpeakerOperation.Volume) Notice = null;
        Notify();
        try
        {
            var before = await ReadAsync(token);
            if (!before.Active)
            {
                SetNotice("스피커가 대기 상태라 조작하지 않았습니다");
                return;
            }
            if (before.SourceId != observed.SourceId)
            {
                SetNotice("다른 곳에서 입력을 바꿔서 조작을 취소했습니다");
                return;
            }
            await body(before, token);
        }
        catch (Exception e) when (token == generation)
        {
            failures = Math.Max(failures, 1);
            Fail("조작 중지 (자동 재시도 없음): " + Describe(e));
            SetNotice("조작을 멈췄습니다: " + Describe(e), error: true);
        }
        catch
        {
        }
        finally
        {
            if (token == generation)
            {
                Busy = false;
                Operation = SpeakerOperation.None;
                OperationSource = null;
            }
            Notify();
            if (token == generation) DrainPendingVolume();
        }
    }

    Task Verify(int token, Func<SpeakerState, bool> predicate) =>
        Verify(token, predicate, VerifyAttempts, VerifyDelayMs, tolerateTransient: false);

    /// <param name="tolerateTransient">
    /// Power changes take seconds and the device may answer mid-transition. Read errors then count as
    /// "not yet" instead of failing at once; the last attempt still fails.
    /// </param>
    async Task Verify(int token, Func<SpeakerState, bool> predicate, int attempts, int delayMs, bool tolerateTransient)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) await Delay(delayMs, cts.Token);
            Check(token);
            try
            {
                if (predicate(await ReadAsync(token))) return;
            }
            catch (Exception e) when (tolerateTransient && i < attempts - 1 && e is SpeakerException or HttpRequestException or TaskCanceledException or TimeoutException && token == generation)
            {
            }
        }
        throw new SpeakerException("스피커 반영을 확인하지 못했습니다");
    }

    async Task<SpeakerState> ReadAsync(int token)
    {
        var s = await Driver!.ReadAsync(cts.Token);
        Check(token);
        if (s.Volume is < 0 or > 100) throw new SpeakerException("음량 응답 범위 오류");
        State = s with { Valid = true, SeenAt = Clock() };
        if (State.Active && State.SourceId != null) LastActiveSource = State.SourceId;
        return State;
    }

    void Check(int token)
    {
        if (token != generation) throw new OperationCanceledException();
    }

    void Fail(string reason)
    {
        State = State with { Valid = false };
        Status = reason;
    }

    void SetNotice(string text, bool error = false)
    {
        Notice = text;
        NoticeIsError = error;
        NoticeAt = Clock();
    }

    void NextGeneration()
    {
        generation++;
        pendingVolume?.Done.TrySetResult();
        pendingVolume = null;
        cts.Cancel();
        cts.Dispose();
        cts = new CancellationTokenSource();
    }

    void Notify() => Changed?.Invoke();

    static string Describe(Exception e) => e switch
    {
        SpeakerException => e.Message,
        TaskCanceledException or TimeoutException => "응답 시간 초과",
        HttpRequestException => "네트워크 연결 실패",
        _ => e.Message,
    };

    public void Dispose()
    {
        Disconnect("종료");
        cts.Dispose();
    }
}
