namespace KefRemote.Core;

/// <summary>
/// Owns the connection to one device and applies the safety policy to every user command.
///
/// Rules (see docs/drivers.md):
/// - Connecting and polling only read. Nothing is written without a user action.
/// - One request at a time. Mute, input and power commands that arrive while busy are dropped.
///   Volume input that arrives while busy is merged into a single latest target, applied once the
///   current request finishes and only if nothing failed or disconnected in between.
/// - Every command re-reads the device first and aborts if the input changed since the user saw it.
/// - Every write is confirmed by reading back. A mutation is never retried.
/// - Any failure invalidates the state, which blocks commands until the next successful poll.
/// - Disconnecting or invalidating bumps a generation counter so in-flight work is discarded.
///
/// Not thread safe: call from a single (UI) synchronization context.
/// </summary>
public sealed class SpeakerController : IDisposable
{
    public const int FreshMilliseconds = 3000;
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
    public string Status { get; private set; } = "연결 전";

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

    public bool Fresh
    {
        get
        {
            long now = Clock();
            return State.Valid && now >= State.SeenAt && now - State.SeenAt < FreshMilliseconds;
        }
    }

    /// <summary>Connected, fresh and powered on. Ignores <see cref="Busy"/> so callers can hold input while a write runs.</summary>
    public bool IsReady => Driver != null && Device != null && Fresh && State.Active;

    public bool CanControl => IsReady && !Busy;

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
            Status = "연결됨";
            return true;
        }
        catch (Exception e) when (token == generation)
        {
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
        Status = reason;
        Notify();
    }

    /// <summary>Discard in-flight work and block commands until the next successful poll.</summary>
    public void Invalidate(string reason)
    {
        NextGeneration();
        State = State with { Valid = false };
        Busy = false;
        Status = reason;
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
            if (previous.Valid && previous.SourceId != State.SourceId) Status = "외부에서 입력이 변경되었습니다";
            else if (!State.Active) Status = "스피커가 대기 상태입니다";
            else if (Status.StartsWith("연결 확인 필요", StringComparison.Ordinal) || Status == "연결 확인 중") Status = "연결됨";
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
        return Run((before, token) => WriteVolume(before, target ?? before.Volume + delta, token));
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
        _ = CompleteWhenDone(p, Run((before, token) => WriteVolume(before, p.Target ?? before.Volume + p.Delta, token)));
    }

    static async Task CompleteWhenDone(PendingVolume p, Task task)
    {
        try { await task; }
        finally { p.Done.TrySetResult(); }
    }

    public Task ToggleMuteAsync() => Run((before, token) => WriteMute(before, !before.Muted, token));

    public Task SetMuteAsync(bool muted) => Run((before, token) => WriteMute(before, muted, token));

    async Task WriteMute(SpeakerState before, bool muted, int token)
    {
        if (!Supports(DriverCapabilities.Mute) || before.Muted == muted) return;
        if (!muted && before.Volume > EffectiveLimit)
        {
            Status = $"음량 {before.Volume}이 상한 {EffectiveLimit}보다 높아 음소거를 해제하지 않았습니다. 먼저 음량을 낮추세요";
            return;
        }
        await Driver!.SetMuteAsync(muted, cts.Token);
        await Verify(token, s => s.SourceId == before.SourceId && s.Muted == muted);
        Status = muted ? "음소거" : "음소거 해제";
    }

    public Task SelectSourceAsync(string sourceId) => Run(async (before, token) =>
    {
        if (!Supports(DriverCapabilities.Source)) return;
        if (!Driver!.Sources.Any(s => s.Id == sourceId)) throw new SpeakerException("지원하지 않는 입력: " + sourceId);
        if (before.SourceId == sourceId) return;
        // Each input can have its own volume, so switching is only done muted.
        if (!Supports(DriverCapabilities.Mute)) throw new SpeakerException("음소거를 지원하지 않는 기기는 안전하게 입력을 전환할 수 없습니다");

        if (!before.Muted)
        {
            await Driver.SetMuteAsync(true, cts.Token);
            await Verify(token, s => s.Muted && s.SourceId == before.SourceId);
        }
        Check(token);
        await Driver.SetSourceAsync(sourceId, cts.Token);
        await Verify(token, s => s.SourceId == sourceId && s.Muted);

        var after = State;
        if (before.Muted)
        {
            Status = "입력 전환 완료 · 기존 음소거 유지";
        }
        else if (after.Volume <= before.Volume && after.Volume <= EffectiveLimit)
        {
            Check(token);
            await Driver.SetMuteAsync(false, cts.Token);
            await Verify(token, s => s.SourceId == sourceId && !s.Muted);
            Status = "입력 전환 완료";
        }
        else
        {
            Status = $"입력 전환 완료 · 전환 후 음량 {after.Volume}이 이전보다 높거나 상한을 넘어 음소거를 유지했습니다";
        }
    });

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
            if (requested > before.Volume) Status = $"앱 음량 상한 {limit}";
            return;
        }
        int tolerance = Driver!.VolumeTolerance;
        await Driver.SetVolumeAsync(target, cts.Token);
        await Verify(token, s => s.SourceId == before.SourceId && Math.Abs(s.Volume - target) <= tolerance);
        Status = target == limit && requested > target ? $"앱 음량 상한 {limit}" : "연결됨";
    }

    async Task Run(Func<SpeakerState, int, Task> body)
    {
        if (!CanControl) return;
        int token = generation;
        var observed = State;
        Busy = true;
        Notify();
        try
        {
            var before = await ReadAsync(token);
            if (!before.Active)
            {
                Status = "스피커가 대기 상태입니다";
                return;
            }
            if (before.SourceId != observed.SourceId)
            {
                Status = "외부에서 입력이 변경되어 조작을 취소했습니다";
                return;
            }
            await body(before, token);
        }
        catch (Exception e) when (token == generation)
        {
            Fail("조작 중지 (자동 재시도 없음): " + Describe(e));
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

    async Task Verify(int token, Func<SpeakerState, bool> predicate)
    {
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) await Delay(180, cts.Token);
            if (predicate(await ReadAsync(token))) return;
        }
        throw new SpeakerException("스피커 반영을 확인하지 못했습니다");
    }

    async Task<SpeakerState> ReadAsync(int token)
    {
        var s = await Driver!.ReadAsync(cts.Token);
        Check(token);
        if (s.Volume is < 0 or > 100) throw new SpeakerException("음량 응답 범위 오류");
        State = s with { Valid = true, SeenAt = Clock() };
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
