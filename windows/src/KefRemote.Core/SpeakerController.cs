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
    Playback,
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
/// - Any failure invalidates the state, which blocks commands until the next successful poll. A speaker that
///   reads fine but is still not on when a power-on stops waiting is reported, not failed.
/// - Disconnecting or invalidating bumps a generation counter so in-flight work is discarded.
/// - Change reports from the device (<see cref="ISpeakerDriver.WaitForChangesAsync"/>) only trigger reads. While they
///   arrive, polling slows to a heartbeat; when they fail, polling takes over at the usual pace.
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
    // A read is five requests. Every half second at most, so a speaker that is busy changing power isn't flooded.
    const int PowerVerifyAttempts = 16, PowerVerifyDelayMs = 500;
    // Waking takes 2 to 4 s right after standby but can take far longer, e.g. after a long standby. A read every
    // half second for the first 5 s so a usual wake shows at once, then one a second, about 30 s in all.
    const int WakeQuickAttempts = 11, WakeAttempts = 36, WakeSlowDelayMs = 1000;
    const int WakingPollMs = 500;
    // With change reports the poll is only a heartbeat that notices a speaker that went away.
    const int HeartbeatVisibleMs = 3000, HeartbeatHiddenMs = 10000;
    static readonly int[] Backoff = [2000, 5000, 10000, 30000];
    static readonly int[] ListenBackoff = [2000, 5000, 10000, 30000, 60000];
    // A play/pause or skip shows on the player within about 1.5 s.
    const int PlaybackConfirmAttempts = 6, PlaybackConfirmDelayMs = 300;

    public static string NotConnected => Loc.T("연결 전", "Not connected", "未接続");
    public static string UnsupportedInput => Loc.T("지원하지 않는 입력: ", "Unsupported input: ", "対応していない入力: ");
    static string Connected => Loc.T("연결됨", "Connected", "接続済み");
    static string InStandby => Loc.T("스피커가 대기 상태입니다", "The speaker is in standby", "スピーカーはスタンバイ中です");
    static string ReachedLimit(int limit) => Loc.T($"상한 {limit}에 닿았습니다", $"Reached the limit {limit}", $"上限 {limit} に達しました");

    int generation;
    int failures;
    int appVolumeLimit = 30;
    CancellationTokenSource cts = new();
    PendingVolume? pendingVolume;
    LateWake? lateWake;
    CancellationTokenSource? listenCts;
    int listenFailures;
    DeviceChanges? heldChanges;
    bool playbackEnabled, playbackReading, playbackAgain, playbackConfirming, playbackDirty;
    long playbackAt;

    /// <summary>
    /// A power-on that stopped waiting before the speaker said it was on: the input it was asked for and the notice
    /// shown then. The first read that finds the speaker on replaces that notice (see <see cref="FinishLateWake"/>).
    /// </summary>
    sealed record LateWake(string? Source, string Notice);

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
    public string Status { get; private set; } = NotConnected;

    /// <summary>Outcome of the last user command when it needs explaining (refused, capped, kept muted, failed).</summary>
    public string? Notice { get; private set; }
    public bool NoticeIsError { get; private set; }

    /// <summary>The notice is about the volume limit (capped, refused or kept muted because of it).</summary>
    public bool NoticeIsLimit { get; private set; }

    /// <summary><see cref="Clock"/> time the notice was set, so the UI can let it expire.</summary>
    public long NoticeAt { get; private set; }

    public SpeakerOperation Operation { get; private set; }

    /// <summary>Input a running source switch or power-on is heading for.</summary>
    public string? OperationSource { get; private set; }

    /// <summary>
    /// The input last seen while the speaker was on. Power-on returns to it, and it names the input of a speaker
    /// that woke on it without saying so (<see cref="SpeakerState.OnPreviousInput"/>).
    /// </summary>
    public string? LastActiveSource { get; private set; }

    /// <summary><see cref="LastActiveSource"/> changed, so the app can keep it for the next launch.</summary>
    public event Action<string>? LastActiveSourceChanged;

    /// <summary>
    /// Raise the volume by briefly writing a higher value first when the driver says the device needs it
    /// (<see cref="ISpeakerDriver.ApproachOvershoot"/>). The overshoot never exceeds <see cref="EffectiveLimit"/>.
    /// </summary>
    public bool ApproachFromAbove { get; set; }

    /// <summary>What the speaker is playing. <see cref="PlaybackInfo.None"/> while <see cref="PlaybackEnabled"/> is off.</summary>
    public PlaybackInfo Playback { get; private set; } = PlaybackInfo.None;

    /// <summary>Read what is playing. Off: nothing is read and <see cref="Playback"/> stays empty.</summary>
    public bool PlaybackEnabled
    {
        get => playbackEnabled;
        set
        {
            if (playbackEnabled == value) return;
            playbackEnabled = value;
            if (value) _ = RefreshPlaybackAsync();
            else SetPlayback(PlaybackInfo.None);
        }
    }

    /// <summary>A track to show: playback is on, the speaker is on a streaming input and something is loaded.</summary>
    public bool ShowsPlayback =>
        playbackEnabled && IsReady && Supports(DriverCapabilities.Playback) && Driver!.SourceHasPlayback(State.SourceId) && Playback.HasTrack;

    /// <summary>Position now, counted on from the last read while playing. -1 when unknown.</summary>
    public long PlaybackPositionMs
    {
        get
        {
            var p = Playback;
            if (p.PositionMs < 0) return -1;
            long position = p.PositionMs + (p.Status == PlaybackStatus.Playing ? Math.Max(0, Clock() - playbackAt) : 0);
            return p.DurationMs > 0 ? Math.Min(position, p.DurationMs) : position;
        }
    }

    /// <summary>Start listening for change reports after connecting, when the driver offers them. Off in tests.</summary>
    public bool ListenForChanges { get; set; } = true;

    /// <summary>Change reports are arriving, so polling is only a heartbeat.</summary>
    public bool ChangesActive { get; private set; }

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

    /// <summary>
    /// Coming out of standby: the input is back but the power status, about 2 s behind it, still says standby.
    /// Commands wait for the power status; the UI already shows the input.
    /// </summary>
    public bool IsWaking => IsStandby && (State.SourceId != null || State.OnPreviousInput);

    public bool CanPowerOn => IsStandby && !IsWaking && !Busy && Supports(DriverCapabilities.Power);

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

    /// <summary>
    /// Replace the driver, identify the device and read once. Never writes. <paramref name="lastInput"/> is the
    /// last input this speaker was seen on before, e.g. in an earlier launch.
    /// </summary>
    public async Task<bool> ConnectAsync(ISpeakerDriver driver, string? lastInput = null)
    {
        Disconnect(Loc.T("연결 확인 중", "Checking the connection", "接続を確認中"));
        Driver = driver;
        LastActiveSource = lastInput;
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
            Status = State.Active ? Connected : InStandby;
            playbackDirty = true;
            StartListening(driver);
            return true;
        }
        catch (Exception e) when (token == generation)
        {
            failures++;
            Fail(Loc.T("연결 실패: ", "Couldn't connect: ", "接続できませんでした: ") + Describe(e));
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
            ReleaseHeldChanges();
        }
    }

    /// <summary>Drop the driver and discard any in-flight work.</summary>
    public void Disconnect(string reason)
    {
        NextGeneration();
        StopListening();
        heldChanges = null;
        Playback = PlaybackInfo.None;
        Driver?.Dispose();
        Driver = null;
        Device = null;
        State = SpeakerState.Unknown;
        Busy = false;
        failures = 0;
        lateWake = null;
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
        NoticeIsLimit = false;
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
                SetNotice(Loc.T("다른 곳에서 입력을 바꿨습니다", "The input was changed elsewhere", "ほかの場所で入力が切り替えられました"));
            Status = State.Active ? Connected : InStandby;
            // Without change reports the player is read with every poll; with them only to correct the position.
            if (!ChangesActive || Playback.Status == PlaybackStatus.Playing) playbackDirty = true;
        }
        catch (Exception e) when (token == generation)
        {
            failures++;
            Fail(Loc.T("연결 확인 필요: ", "Check the connection: ", "接続を確認してください: ") + Describe(e));
        }
        catch
        {
        }
        finally
        {
            if (token == generation) Busy = false;
            Notify();
            if (token == generation) DrainPendingVolume();
            ReleaseHeldChanges();
        }
    }

    /// <summary>Suggested delay before the next poll, with backoff after failures.</summary>
    public TimeSpan NextPollDelay(bool windowVisible) =>
        TimeSpan.FromMilliseconds(failures == 0
            ? IsWaking ? WakingPollMs
                : ChangesActive ? windowVisible ? HeartbeatVisibleMs : HeartbeatHiddenMs
                : windowVisible ? 1500 : 4000
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
            SetNotice(Loc.T(
                $"음량({before.Volume})이 상한({EffectiveLimit})보다 높아 음소거를 해제하지 않았습니다. 먼저 음량을 낮추세요",
                $"Still muted: the volume ({before.Volume}) is above the limit ({EffectiveLimit}). Lower the volume first",
                $"音量（{before.Volume}）が上限（{EffectiveLimit}）を超えているため、ミュートを解除しませんでした。先に音量を下げてください"), limit: true);
            return;
        }
        await Driver!.SetMuteAsync(muted, cts.Token);
        await Verify(token, s => s.SourceId == before.SourceId && s.Muted == muted);
    }

    public Task SelectSourceAsync(string sourceId) => Run(SpeakerOperation.Source, sourceId, async (before, token) =>
    {
        if (!Supports(DriverCapabilities.Source)) return;
        if (!Driver!.Sources.Any(s => s.Id == sourceId)) throw new SpeakerException(UnsupportedInput + sourceId);
        await SwitchSource(before, sourceId, token);
    });

    /// <summary>
    /// Each input can have its own volume, so switching is only done muted:
    /// mute, verify, switch, verify, then unmute only if the new input is not louder and within the limit.
    /// </summary>
    /// <param name="restoring">
    /// The input the speaker should already be on is written again after a power-on, so staying muted as before
    /// needs no notice.
    /// </param>
    async Task SwitchSource(SpeakerState before, string sourceId, int token, bool restoring = false)
    {
        // An input only shown as the last one (OnPreviousInput) is written anyway, so the speaker is surely on it.
        if (before.SourceId == sourceId && !before.OnPreviousInput) return;
        if (!Supports(DriverCapabilities.Mute)) throw new SpeakerException(Loc.T("음소거를 지원하지 않는 기기는 안전하게 입력을 전환할 수 없습니다", "Inputs can't be switched safely on a device without mute", "ミュートに対応していない機器では、入力を安全に切り替えられません"));

        if (!before.Muted)
        {
            await Driver!.SetMuteAsync(true, cts.Token);
            await Verify(token, s => s.Muted && s.SourceId == before.SourceId);
        }
        Check(token);
        await Driver!.SetSourceAsync(sourceId, cts.Token);
        // Only an input the speaker names counts: one shown as the last input would pass at once. Leaving an
        // unnamed input may take a while, as power changes do.
        if (before.OnPreviousInput)
            await Verify(token, s => s.SourceId == sourceId && s.Muted && !s.OnPreviousInput, PowerVerifyAttempts, PowerVerifyDelayMs, tolerateTransient: true);
        else
            await Verify(token, s => s.SourceId == sourceId && s.Muted && !s.OnPreviousInput);

        var after = State;
        if (before.Muted)
        {
            if (!restoring) SetNotice(Loc.T("입력을 바꿨습니다. 원래 음소거 상태를 유지합니다", "Input switched. It stays muted as before", "入力を切り替えました。ミュートは元のままです"));
        }
        else if (after.Volume <= before.Volume && after.Volume <= EffectiveLimit)
        {
            Check(token);
            await Driver.SetMuteAsync(false, cts.Token);
            await Verify(token, s => s.SourceId == sourceId && !s.Muted);
        }
        else
        {
            SetNotice(Loc.T(
                $"새 입력의 음량({after.Volume})이 이전보다 높거나 상한을 넘어 음소거를 유지했습니다",
                $"Kept muted: the new input's volume ({after.Volume}) is higher than before or above the limit",
                $"新しい入力の音量（{after.Volume}）が前より高いか上限を超えているため、ミュートのままにしました"), limit: true);
        }
    }

    /// <summary>Put the speaker in standby. Verified by reading the power state back.</summary>
    public Task PowerOffAsync() => Run(SpeakerOperation.PowerOff, null, async (before, token) =>
    {
        if (!Supports(DriverCapabilities.Power))
        {
            SetNotice(Loc.T("이 기기는 앱에서 전원을 끌 수 없습니다", "This device can't be turned off from the app", "この機器はアプリから電源を切れません"));
            return;
        }
        await Driver!.SetPowerAsync(false, cts.Token);
        await Verify(token, s => s.Power == PowerState.Standby, PowerVerifyAttempts, PowerVerifyDelayMs, tolerateTransient: true);
        Status = InStandby;
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
        lateWake = null;
        // Set from the power write until the speaker says it is on. The write can reach the speaker even when
        // its reply is lost.
        bool awaitingOn = false;
        Notify();
        try
        {
            var before = await ReadAsync(token);
            if (sourceId != null && !Driver!.Sources.Any(s => s.Id == sourceId)) throw new SpeakerException(UnsupportedInput + sourceId);
            if (!before.Active)
            {
                if (before.Volume > EffectiveLimit)
                {
                    SetNotice(Loc.T(
                        $"대기 전 음량({before.Volume})이 상한({EffectiveLimit})보다 높아 켜지 않았습니다. 리모컨이나 KEF Connect로 음량을 낮춘 뒤 켜세요",
                        $"Not turned on: the volume before standby ({before.Volume}) is above the limit ({EffectiveLimit}). Lower it with the remote or KEF Connect first",
                        $"スタンバイ前の音量（{before.Volume}）が上限（{EffectiveLimit}）を超えているため、電源を入れませんでした。リモコンかKEF Connectで音量を下げてから入れてください"), limit: true);
                    return;
                }
                awaitingOn = true;
                await Driver!.SetPowerAsync(true, cts.Token);
                bool confirmed = await TryVerify(token, s =>
                {
                    // Show the input the speaker woke on as soon as it is back, before the power status follows.
                    if (sourceId == null && s.SourceId != null && s.SourceId != OperationSource)
                    {
                        OperationSource = s.SourceId;
                        Notify();
                    }
                    // The power status alone says it is on. The input may stay unnamed for good (OnPreviousInput
                    // before any input was seen), so it isn't waited for.
                    return s.Active;
                }, WakeAttempts, i => i < WakeQuickAttempts ? PowerVerifyDelayMs : WakeSlowDelayMs, tolerateTransient: true);
                if (!confirmed)
                {
                    // Every read worked, the speaker just isn't on yet. Not a failure: the state stays valid, polling
                    // shows the speaker as on once it says so and replaces this notice then.
                    bool waking = IsWaking;
                    string text = (waking
                        ? Loc.T("스피커가 아직 켜지는 중입니다. 켜지면 바로 표시합니다", "The speaker is still turning on. It shows as on as soon as it is", "スピーカーはまだ起動中です。電源が入るとすぐに表示されます")
                        : Loc.T("스피커가 아직 켜지지 않았습니다. 켜지면 바로 표시합니다", "The speaker hasn't turned on yet. It shows as on as soon as it is", "スピーカーの電源はまだ入っていません。入るとすぐに表示されます"))
                        + (sourceId != null ? Loc.T(" 입력은 켜진 뒤 다시 고르세요", " Pick the input again once it's on", " 電源が入ってから入力をもう一度選んでください") : "");
                    SetNotice(text, error: !waking);
                    lateWake = new LateWake(sourceId, text);
                    return;
                }
                awaitingOn = false;
                Status = Connected;
            }
            var on = State;
            if (on.Volume > EffectiveLimit)
            {
                SetNotice(Loc.T(
                    $"켜졌습니다. 음량({on.Volume})이 상한({EffectiveLimit})보다 높으니 먼저 낮추세요",
                    $"Turned on. The volume ({on.Volume}) is above the limit ({EffectiveLimit}); lower it first",
                    $"電源が入りました。音量（{on.Volume}）が上限（{EffectiveLimit}）を超えているので、先に下げてください"), limit: true);
                return;
            }
            if (sourceId != null && Supports(DriverCapabilities.Source))
            {
                Check(token);
                OperationSource = sourceId;
                await SwitchSource(on, sourceId, token);
            }
            else if (on.OnPreviousInput && on.SourceId is { } last && Supports(DriverCapabilities.Source))
            {
                // Woken without naming its input: the last input is written with the usual muted switch, so the
                // speaker is surely on it and says so. Unknown, it is left as it is.
                Check(token);
                OperationSource = last;
                try
                {
                    await SwitchSource(on, last, token, restoring: true);
                }
                catch (SpeakerException) when (token == generation && State.Valid)
                {
                    // The speaker is on and reads fine; polls notice if it went away.
                    SetNotice(Loc.T("켜졌지만 입력을 다시 지정하지 못했습니다. 음소거된 채일 수 있으니 입력을 직접 고르세요", "Turned on, but the input couldn't be set again and may still be muted. Pick the input yourself", "電源は入りましたが、入力を指定し直せませんでした。ミュートのままの可能性があるので、入力を選んでください"));
                }
            }
        }
        catch (Exception e) when (token == generation)
        {
            failures = Math.Max(failures, 1);
            Fail(Loc.T("조작 중지 (자동 재시도 없음): ", "Stopped (no automatic retry): ", "操作を停止しました（自動再試行なし）: ") + Describe(e));
            SetNotice(Loc.T("스피커를 켜지 못했습니다: ", "Couldn't turn the speaker on: ", "スピーカーの電源を入れられませんでした: ") + Describe(e), error: true);
            if (awaitingOn) lateWake = new LateWake(sourceId, Notice!);
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
            ReleaseHeldChanges();
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
                SetNotice(before.Volume > limit ? Loc.T($"음량이 상한 {limit}보다 높아 올리지 않았습니다", $"Not raised: the volume is above the limit {limit}", $"音量が上限 {limit} を超えているため上げませんでした") : ReachedLimit(limit), limit: true);
            return;
        }
        if (target == limit && requested > target) SetNotice(ReachedLimit(limit), limit: true);

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
                SetNotice(Loc.T("스피커가 대기 상태라 조작하지 않았습니다", "Nothing changed: the speaker is in standby", "スピーカーがスタンバイ中のため操作しませんでした"));
                return;
            }
            if (before.SourceId != observed.SourceId)
            {
                SetNotice(Loc.T("다른 곳에서 입력을 바꿔서 조작을 취소했습니다", "Cancelled: the input was changed elsewhere", "ほかの場所で入力が切り替えられたため、操作を取り消しました"));
                return;
            }
            await body(before, token);
        }
        catch (Exception e) when (token == generation)
        {
            failures = Math.Max(failures, 1);
            Fail(Loc.T("조작 중지 (자동 재시도 없음): ", "Stopped (no automatic retry): ", "操作を停止しました（自動再試行なし）: ") + Describe(e));
            SetNotice(Loc.T("조작을 멈췄습니다: ", "Stopped: ", "操作を停止しました: ") + Describe(e), error: true);
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
            ReleaseHeldChanges();
        }
    }

    Task Verify(int token, Func<SpeakerState, bool> predicate) =>
        Verify(token, predicate, VerifyAttempts, VerifyDelayMs, tolerateTransient: false);

    async Task Verify(int token, Func<SpeakerState, bool> predicate, int attempts, int delayMs, bool tolerateTransient)
    {
        if (!await TryVerify(token, predicate, attempts, _ => delayMs, tolerateTransient))
            throw new SpeakerException(Loc.T("스피커 반영을 확인하지 못했습니다", "Couldn't confirm the change on the speaker", "スピーカーへの反映を確認できませんでした"));
    }

    /// <summary>Reads until <paramref name="predicate"/> holds. False when the last read worked but it never held.</summary>
    /// <param name="delayMs">Pause before each attempt after the first, by attempt index.</param>
    /// <param name="tolerateTransient">
    /// Power changes take seconds and the device may answer mid-transition. Read errors then count as
    /// "not yet" instead of failing at once; an error on the last attempt still throws.
    /// </param>
    async Task<bool> TryVerify(int token, Func<SpeakerState, bool> predicate, int attempts, Func<int, int> delayMs, bool tolerateTransient)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) await Delay(delayMs(i), cts.Token);
            Check(token);
            try
            {
                if (predicate(await ReadAsync(token))) return true;
            }
            catch (Exception e) when (tolerateTransient && i < attempts - 1 && e is SpeakerException or HttpRequestException or TaskCanceledException or TimeoutException && token == generation)
            {
            }
        }
        return false;
    }

    async Task<SpeakerState> ReadAsync(int token)
    {
        var s = await Driver!.ReadAsync(cts.Token);
        Check(token);
        if (s.Volume is < 0 or > 100) throw new SpeakerException(Loc.T("음량 응답 범위 오류", "Volume reply out of range", "音量の応答が範囲外です"));
        // On the input it had before standby without saying which: the last input seen while on, if it is one of
        // the device's. Every read names it the same way, so it never looks like an input changed elsewhere.
        if (s.SourceId == null && s.OnPreviousInput && LastActiveSource is { } last && Driver.Sources.Any(x => x.Id == last))
            s = s with { SourceId = last };
        if (s.SourceId != State.SourceId || s.Power != State.Power) playbackDirty = true;
        State = s with { Valid = true, SeenAt = Clock() };
        if (State.Active && State.SourceId is { } source && source != LastActiveSource)
        {
            LastActiveSource = source;
            LastActiveSourceChanged?.Invoke(source);
        }
        if (State.Active && lateWake is { } wake) FinishLateWake(wake);
        return State;
    }

    /// <summary>
    /// The speaker came on after a power-on stopped waiting for it. The notice from then no longer holds: it goes,
    /// or says the input it was asked for wasn't switched. Left alone if a newer notice replaced it.
    /// </summary>
    void FinishLateWake(LateWake wake)
    {
        lateWake = null;
        if (Notice != wake.Notice) return;
        if (wake.Source != null && wake.Source != State.SourceId)
        {
            SetNotice(Loc.T("켜졌습니다. 늦게 켜져서 입력은 바꾸지 않았습니다", "Turned on. It took a while, so the input wasn't switched", "電源が入りました。時間がかかったため、入力は切り替えていません"));
            return;
        }
        Notice = null;
        NoticeIsError = false;
        NoticeIsLimit = false;
    }

    /// <summary>Play/pause, next or previous on the speaker's own player. Confirmed by reading the player back.</summary>
    public Task PlaybackAsync(PlaybackCommand command) => Run(SpeakerOperation.Playback, null, async (before, token) =>
    {
        if (!Supports(DriverCapabilities.Playback)) return;
        var shown = Playback;
        long shownPosition = PlaybackPositionMs;
        var driver = Driver!;
        try
        {
            await driver.SendPlaybackAsync(command, cts.Token);
        }
        catch (SpeakerException e)
        {
            // The player turned the command down (e.g. a source without skipping). The connection is fine.
            SetNotice(Loc.T("스피커가 재생 명령을 받지 않았습니다: ", "The speaker didn't take the playback command: ", "スピーカーが再生コマンドを受け付けませんでした: ") + e.Message);
            return;
        }
        Check(token);
        if (command == PlaybackCommand.TogglePause && shown.HasTrack)
        {
            // Show the new state at once, like the system media controls; the reads below confirm it.
            SetPlayback(shown with
            {
                Status = shown.Status == PlaybackStatus.Playing ? PlaybackStatus.Paused : PlaybackStatus.Playing,
                PositionMs = shownPosition,
            });
        }
        _ = ConfirmPlaybackAsync(driver, token, command, shown, shownPosition);
    });

    /// <summary>Reads the player until the command shows, without holding up other commands.</summary>
    async Task ConfirmPlaybackAsync(ISpeakerDriver driver, int token, PlaybackCommand command, PlaybackInfo shown, long shownPosition)
    {
        playbackConfirming = true;
        PlaybackInfo? last = null;
        try
        {
            for (int i = 0; i < PlaybackConfirmAttempts; i++)
            {
                await Delay(PlaybackConfirmDelayMs, cts.Token);
                if (token != generation || Driver != driver) return;
                last = await driver.ReadPlaybackAsync(cts.Token);
                if (token != generation || Driver != driver) return;
                bool done = command switch
                {
                    PlaybackCommand.TogglePause => last.Status != shown.Status,
                    _ => last.Title != shown.Title || last.Artist != shown.Artist
                        || (last.PositionMs >= 0 && shownPosition >= 0 && last.PositionMs + 1500 < shownPosition),
                };
                if (done) break;
            }
        }
        catch
        {
            // Playback is extra information. The regular poll reports connection problems.
        }
        finally
        {
            if (token == generation && Driver == driver)
            {
                playbackConfirming = false;
                if (last != null && playbackEnabled) SetPlayback(last);
                if (playbackAgain) _ = RefreshPlaybackAsync();
            }
        }
    }

    /// <summary>Read what is playing. Read only; failures keep what is shown and are left to the regular poll.</summary>
    public async Task RefreshPlaybackAsync()
    {
        if (!playbackEnabled || Driver is not { } driver || Device == null || !Supports(DriverCapabilities.Playback)) return;
        if (!State.Active || !driver.SourceHasPlayback(State.SourceId))
        {
            if (Playback != PlaybackInfo.None) SetPlayback(PlaybackInfo.None);
            return;
        }
        if (playbackReading || playbackConfirming)
        {
            playbackAgain = true;
            return;
        }
        playbackReading = true;
        int token = generation;
        try
        {
            do
            {
                playbackAgain = false;
                var info = await driver.ReadPlaybackAsync(cts.Token);
                if (token != generation || Driver != driver || !playbackEnabled) return;
                SetPlayback(info);
            }
            while (playbackAgain && !playbackConfirming);
        }
        catch
        {
        }
        finally
        {
            playbackReading = false;
        }
    }

    void SetPlayback(PlaybackInfo info)
    {
        Playback = info;
        playbackAt = Clock();
        Notify();
    }

    void StartListening(ISpeakerDriver driver)
    {
        StopListening();
        if (!ListenForChanges || !driver.Capabilities.HasFlag(DriverCapabilities.Changes)) return;
        var source = new CancellationTokenSource();
        listenCts = source;
        _ = ListenAsync(driver, source.Token);
    }

    void StopListening()
    {
        listenCts?.Cancel();
        listenCts?.Dispose();
        listenCts = null;
        listenFailures = 0;
        ChangesActive = false;
    }

    /// <summary>
    /// Waits for change reports for as long as <paramref name="driver"/> is connected. Has its own cancellation:
    /// invalidating after a failed command must not stop it.
    /// </summary>
    async Task ListenAsync(ISpeakerDriver driver, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Driver == driver)
        {
            long started = Clock();
            DeviceChanges changes;
            try
            {
                changes = await driver.WaitForChangesAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested && Driver == driver)
            {
                listenFailures++;
                if (ChangesActive)
                {
                    ChangesActive = false;
                    Notify();
                }
                // Find out at once whether the speaker is gone or only its change reports stopped.
                await PollAsync();
                if (!await Pause(ListenBackoff[Math.Min(listenFailures, ListenBackoff.Length) - 1], ct)) return;
                continue;
            }
            catch
            {
                return;
            }
            if (ct.IsCancellationRequested || Driver != driver) return;
            listenFailures = 0;
            if (!ChangesActive)
            {
                ChangesActive = true;
                Notify();
            }
            await ApplyChangesAsync(changes);
            // A device that answers at once with nothing would otherwise be asked again in a tight loop.
            if (!changes.Any && Clock() - started < 1000 && !await Pause(1000, ct)) return;
        }
    }

    async Task<bool> Pause(int ms, CancellationToken ct)
    {
        try
        {
            await Delay(ms, ct);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Acts on a change report. Reads only when the report says something the state doesn't show yet, so the echo
    /// of the app's own verified write costs nothing. While busy the report is held and merged, and looked at again
    /// once the running command or read has finished.
    /// </summary>
    async Task ApplyChangesAsync(DeviceChanges changes)
    {
        if (Driver == null || Device == null) return;
        if (Busy)
        {
            heldChanges = heldChanges == null ? changes : Merge(heldChanges, changes);
            return;
        }
        if (changes.Device)
        {
            await RefreshDeviceAsync();
            changes = changes with { Device = false };
            if (Busy)
            {
                heldChanges = heldChanges == null ? changes : Merge(heldChanges, changes);
                return;
            }
        }
        bool read = changes.State || !State.Valid
            || changes.Volume is int v && v != State.Volume
            || changes.Muted is bool m && m != State.Muted;
        if (read) await PollAsync();
        if (changes.Playback) await RefreshPlaybackAsync();
    }

    void ReleaseHeldChanges()
    {
        if (heldChanges is not { } held || Busy) return;
        heldChanges = null;
        _ = ApplyChangesAsync(held);
    }

    static DeviceChanges Merge(DeviceChanges a, DeviceChanges b) => new()
    {
        Volume = b.Volume ?? a.Volume,
        Muted = b.Muted ?? a.Muted,
        State = a.State || b.State,
        Device = a.Device || b.Device,
        Playback = a.Playback || b.Playback,
    };

    void Check(int token)
    {
        if (token != generation) throw new OperationCanceledException();
    }

    void Fail(string reason)
    {
        State = State with { Valid = false };
        Status = reason;
    }

    void SetNotice(string text, bool error = false, bool limit = false)
    {
        Notice = text;
        NoticeIsError = error;
        NoticeIsLimit = limit;
        NoticeAt = Clock();
    }

    void NextGeneration()
    {
        generation++;
        // A confirmation still running for the old generation stops at its token check and leaves this alone.
        playbackConfirming = false;
        pendingVolume?.Done.TrySetResult();
        pendingVolume = null;
        cts.Cancel();
        cts.Dispose();
        cts = new CancellationTokenSource();
    }

    void Notify()
    {
        Changed?.Invoke();
        if (playbackDirty && !Busy && Driver != null)
        {
            playbackDirty = false;
            _ = RefreshPlaybackAsync();
        }
    }

    static string Describe(Exception e) => e switch
    {
        SpeakerException => e.Message,
        TaskCanceledException or TimeoutException => Loc.T("응답 시간 초과", "Timed out", "応答がタイムアウトしました"),
        HttpRequestException => Loc.T("네트워크 연결 실패", "Network connection failed", "ネットワークに接続できませんでした"),
        _ => e.Message,
    };

    public void Dispose()
    {
        Disconnect(Loc.T("종료", "Closed", "終了"));
        cts.Dispose();
    }
}
