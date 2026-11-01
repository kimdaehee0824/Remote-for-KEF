import Observation

/// Serializes every read and write to one speaker and owns the safety rules
/// from docs/drivers.md:
/// - Connecting, reconnecting and polling only read. Nothing is written
///   unless the user asked for it.
/// - Every write is preceded by a fresh read and followed by read-back
///   verification. A write is never retried; an unverified result drops the
///   connection and reconnects read-only. A speaker that reads fine but is
///   still not on when a wake stops waiting is reported, not dropped.
/// - A connection loss, an external input change or a settings change
///   invalidates queued commands. Nothing is replayed after reconnecting.
/// - One failed status read keeps the last state and is retried a second
///   later; the second in a row shows the speaker as offline. Starting or
///   waking retries a few times before showing offline.
/// - Change reports from the device (`SpeakerDriver.waitForChanges`) only
///   trigger reads. While they arrive, polling slows to a heartbeat; when they
///   fail, polling takes over at the usual pace.
@MainActor
@Observable
public final class SpeakerController {
    public enum Phase: Equatable, Sendable {
        case idle
        case connecting
        case online
        case offline(String)
    }

    /// What the command in progress is doing, for progress display.
    public enum Activity: Equatable, Sendable {
        case volume
        case mute
        case source(String)
        /// Waking, then switching to the input if one was given.
        case powerOn(String?)
        case powerOff
        case playback(PlaybackCommand)
    }

    public private(set) var phase: Phase = .idle
    public private(set) var device: DeviceInfo?
    public private(set) var snapshot: SpeakerSnapshot?
    public private(set) var isBusy = false
    public private(set) var activity: Activity?
    /// Target of the volume command being processed, for display while the
    /// speaker has not confirmed it yet.
    public private(set) var pendingVolume: Int?
    /// True while a queued or running volume command will also unmute.
    public private(set) var pendingUnmute = false
    /// Last user-facing message about a refused or stopped command.
    public private(set) var notice: String?
    /// Last input seen while powered on. Used to wake from standby.
    public private(set) var lastActiveSource: String?
    /// What the speaker is playing. `.none` while `playbackEnabled` is off.
    public private(set) var playback: PlaybackInfo = .none
    /// Change reports are arriving, so polling is only a heartbeat.
    public private(set) var changesActive = false

    /// Read what is playing. Off: nothing is read and `playback` stays empty.
    public var playbackEnabled = false {
        didSet {
            guard playbackEnabled != oldValue else { return }
            if playbackEnabled { Task { await refreshPlayback() } } else { setPlayback(.none) }
        }
    }
    /// Listen for change reports when the driver offers them. Off in tests.
    public var listensForChanges = true
    /// Seconds since boot, for counting the play position on. Replaceable for tests.
    @ObservationIgnored public var uptime: () -> Double = { monotonicSeconds() }

    /// Clamped to 0...100 where it is used.
    public var appLimit: Int = VolumePolicy.defaultAppLimit
    /// Shorter polling while the menu panel is open.
    public var isPanelVisible = false
    /// Raise through the driver's overshoot value so the level matches the one
    /// reached from above (see `SpeakerDriver.approachOvershoot`).
    public var approachFromAbove = false

    public var effectiveLimit: Int {
        VolumePolicy.effectiveLimit(appLimit: appLimit, deviceLimit: device?.deviceVolumeLimit)
    }
    public var isOnline: Bool { phase == .online }
    /// Coming out of standby: the input is back but the power status, about
    /// 2 s behind it, still says standby. Commands wait for the power status;
    /// the panel already shows the input.
    public var isWaking: Bool { isOnline && snapshot?.power == .standby && snapshot?.sourceId != nil }
    public var isAboveLimit: Bool { (snapshot?.volume ?? 0) > effectiveLimit }
    /// True when the KEF device maximum, not the app limit, is what stops the volume.
    public var limitFromDevice: Bool {
        guard let deviceLimit = device?.deviceVolumeLimit else { return false }
        return deviceLimit < VolumePolicy.clamp(appLimit)
    }

    /// A track to show: playback is on, the speaker is on a streaming input and something is loaded.
    public var showsPlayback: Bool {
        guard playbackEnabled, isOnline, let driver, let snapshot, snapshot.power == .on,
              device?.capabilities.contains(.playback) == true else { return false }
        return driver.sourceHasPlayback(snapshot.sourceId) && playback.hasTrack
    }

    /// Position now in milliseconds, counted on from the last read while playing. -1 when unknown.
    public func playbackPositionMs(at time: Double? = nil) -> Int {
        let p = playback
        guard p.positionMs >= 0 else { return -1 }
        var position = p.positionMs
        if p.status == .playing { position += Int(max(0, (time ?? uptime()) - playbackReadAt) * 1000) }
        return p.durationMs > 0 ? min(position, p.durationMs) : position
    }

    private enum Intent: Equatable {
        case volume(Int)
        /// Sets the volume, then unmutes: volume up while muted, like the system's keys.
        case volumeUnmuting(Int)
        case volumeStep(Int)
        case toggleMute
        case source(String)
        case standby
        case wake(String?)
        case playback(PlaybackCommand)

        var isVolume: Bool {
            switch self {
            case .volume, .volumeUnmuting, .volumeStep: true
            default: false
            }
        }

        var activity: Activity {
            switch self {
            case .volume, .volumeUnmuting, .volumeStep: .volume
            case .toggleMute: .mute
            case let .source(id): .source(id)
            case .standby: .powerOff
            case let .wake(id): .powerOn(id)
            case let .playback(command): .playback(command)
            }
        }
    }

    private struct Job {
        var intent: Intent
        let expectedSource: String?
        let token: Int
    }

    private struct Cancelled: Error {}

    private struct Stop: Error {
        let message: String
    }

    // Bookkeeping, not state shown on screen. Ignored by observation so it
    // never makes a view draw again.
    @ObservationIgnored private var driver: (any SpeakerDriver)?
    /// Bumped whenever earlier reads or intents must no longer be trusted.
    @ObservationIgnored private var generation = 0
    /// Bumped whenever a command applies a read, so an older poll cannot
    /// overwrite it.
    @ObservationIgnored private var stateEpoch = 0
    @ObservationIgnored private var queued: Job?
    /// A wake that stopped waiting before the speaker said it was on: the
    /// input it was asked for and the notice shown then. The first read that
    /// finds the speaker on replaces that notice (see `finishLateWake`).
    @ObservationIgnored private var lateWake: (source: String?, notice: String)?
    /// Failed connection attempts in a row.
    @ObservationIgnored private var failures = 0
    /// Failed status reads in a row while online.
    @ObservationIgnored private var missedPolls = 0
    @ObservationIgnored private var suspended = false
    @ObservationIgnored private var monitorTask: Task<Void, Never>?
    @ObservationIgnored private var listenTask: Task<Void, Never>?
    /// Bumped when the driver is replaced or dropped, so its change reports
    /// and player reads are no longer applied.
    @ObservationIgnored private var driverEpoch = 0
    /// Change reports that arrived while a command ran, looked at once it has finished.
    @ObservationIgnored private var heldChanges: DeviceChanges?
    @ObservationIgnored private var playbackReadAt: Double = 0
    @ObservationIgnored private var playbackReading = false
    @ObservationIgnored private var playbackAgain = false
    @ObservationIgnored private var playbackConfirming = false
    /// Internal so tests can wait for queued commands to finish.
    @ObservationIgnored private(set) var drainTask: Task<Void, Never>?
    private let sleep: @Sendable (Duration) async throws -> Void

    let verifyAttempts = 4
    let verifyDelay: Duration = .milliseconds(180)
    /// Every half second at most, so a speaker that is busy changing power
    /// isn't flooded with reads.
    let powerVerifyAttempts = 10
    let powerVerifyDelay: Duration = .milliseconds(500)
    /// Waking takes 2 to 4 s right after standby but can take far longer,
    /// e.g. after a long standby. A read every half second for the first 5 s
    /// so a usual wake shows at once, then one a second, about 30 s in all.
    let wakeQuickAttempts = 11
    let wakeAttempts = 36
    let wakeSlowDelay: Duration = .seconds(1)
    /// Poll interval while waking, so the speaker shows as on soon after it says so.
    let wakingPoll: Duration = .milliseconds(500)
    /// How long the overshoot value is held before the target is written.
    let approachHold: Duration = .milliseconds(100)
    /// A status read that fails once while online is retried before the
    /// speaker is shown as offline. A slow reply is not a lost connection.
    let toleratedMissedPolls = 1
    /// Attempts while starting or waking before the speaker is shown as
    /// offline. The network often comes up a few seconds after login or wake.
    let connectAttempts = 4
    /// Delay between those retries.
    let quickRetry: Duration = .seconds(1)
    /// With change reports the poll is only a heartbeat that notices a speaker that went away.
    let heartbeatVisible: Duration = .seconds(3)
    let heartbeatHidden: Duration = .seconds(10)
    let listenBackoff: [Duration] = [.seconds(2), .seconds(5), .seconds(10), .seconds(30), .seconds(60)]
    /// A play/pause or skip shows on the player within about 1.5 s.
    let playbackConfirmAttempts = 6
    let playbackConfirmDelay: Duration = .milliseconds(300)

    public init(sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }) {
        self.sleep = sleep
    }

    // MARK: Connection

    /// Replaces the driver and connects read-only.
    public func connect(_ driver: any SpeakerDriver) async {
        stopListening()
        driverEpoch += 1
        self.driver = driver
        update(\.device, nil)
        update(\.snapshot, nil)
        // A confirmation still running for the old driver stops at the epoch check and leaves this alone.
        playbackConfirming = false
        setPlayback(.none)
        failures = 0
        missedPolls = 0
        update(\.phase, .connecting)
        await reconnect()
    }

    public func disconnect() {
        invalidate()
        stopListening()
        driverEpoch += 1
        heldChanges = nil
        playbackConfirming = false
        setPlayback(.none)
        lateWake = nil
        driver = nil
        update(\.device, nil)
        update(\.snapshot, nil)
        update(\.phase, .idle)
    }

    /// For system sleep. Stops polling and drops queued commands.
    public func suspend() {
        suspended = true
        invalidate()
        stopListening()
    }

    /// After wake, the state is re-read before anything else happens.
    public func resume() {
        suspended = false
        failures = 0
        missedPolls = 0
        if driver != nil, phase == .online { phase = .connecting }
    }

    @discardableResult
    public func refresh() async -> Bool {
        guard let driver, !isBusy else { return false }
        guard phase == .online else { return await reconnect() }
        let token = generation
        let epoch = stateEpoch
        do {
            let state = try await driver.read()
            guard token == generation else { return false }
            // A command finished while this read was in flight; its read is newer.
            guard epoch == stateEpoch, !isBusy else { return true }
            if let previous = snapshot, previous.sourceId != state.sourceId, queued != nil {
                queued = nil
                notice = loc("외부에서 입력이 바뀌어 대기 중인 명령을 취소했습니다", "Cancelled the waiting command: the input was changed elsewhere", "ほかの場所で入力が切り替えられたため、待機中の操作を取り消しました")
            }
            apply(state)
            failures = 0
            missedPolls = 0
            return true
        } catch {
            guard token == generation else { return false }
            missedPolls += 1
            // Polls only read, and every command reads again before it writes,
            // so keeping the last state through one slow reply is safe.
            if missedPolls <= toleratedMissedPolls { return false }
            goOffline(loc("연결 확인 필요: \(describe(error))", "Check the connection: \(describe(error))", "接続を確認してください: \(describe(error))"))
            return false
        }
    }

    /// Reads the device information again, for the KEF maximum volume that can
    /// change in KEF Connect at any time. Read-only; skipped while a command runs.
    public func refreshDevice() async {
        guard let driver, phase == .online, !isBusy else { return }
        let token = generation
        guard let info = try? await driver.connect(), token == generation else { return }
        update(\.device, info)
    }

    @discardableResult
    private func reconnect() async -> Bool {
        guard let driver else { return false }
        invalidate()
        let token = generation
        do {
            let info = try await driver.connect()
            guard token == generation else { return false }
            let state = try await driver.read()
            guard token == generation else { return false }
            update(\.device, info)
            apply(state)
            update(\.phase, .online)
            failures = 0
            missedPolls = 0
            startListening()
            Task { await refreshPlayback() }
            return true
        } catch {
            guard token == generation else { return false }
            failures += 1
            if phase == .connecting {
                // While starting or waking, keep showing "connecting" for a few quick retries,
                // then fall back to the regular backoff from its first step.
                if failures < connectAttempts { return false }
                failures = 1
            }
            update(\.phase, .offline(describe(error)))
            return false
        }
    }

    public func startMonitoring() {
        monitorTask?.cancel()
        monitorTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                let delay = await self.monitorStep()
                try? await self.sleep(delay)
            }
        }
    }

    public func stopMonitoring() {
        monitorTask?.cancel()
        monitorTask = nil
    }

    /// Pause after a status read that worked. Internal for tests.
    func healthyPollDelay() -> Duration {
        if isWaking { return wakingPoll }
        if changesActive { return isPanelVisible ? heartbeatVisible : heartbeatHidden }
        return isPanelVisible ? .milliseconds(1500) : .seconds(4)
    }

    private func monitorStep() async -> Duration {
        guard driver != nil, !suspended else { return .seconds(1) }
        if isBusy { return .milliseconds(500) }
        if await refresh() {
            // Without change reports the player is read with every poll; with them only to correct the position.
            if playbackEnabled, !isWaking, !changesActive || playback.status == .playing { Task { await refreshPlayback() } }
            return healthyPollDelay()
        }
        // A tolerated missed poll, or a retry while starting or waking.
        if phase == .online || phase == .connecting { return quickRetry }
        // 2 → 5 → 10 → 30 seconds, without user-facing popups.
        let steps: [Duration] = [.seconds(2), .seconds(5), .seconds(10), .seconds(30)]
        return steps[min(max(failures, 1) - 1, steps.count - 1)]
    }

    // MARK: Commands

    public func setVolume(_ volume: Int) {
        enqueue(.volume(VolumePolicy.clamp(volume)))
    }

    /// Sets the volume and then unmutes, in one command, so it can't be dropped
    /// behind a separate mute command. The volume is written while still muted.
    public func setVolumeUnmuting(_ volume: Int) {
        enqueue(.volumeUnmuting(VolumePolicy.clamp(volume)))
    }

    /// Relative step, resolved against the volume read right before writing.
    public func stepVolume(_ delta: Int) {
        enqueue(.volumeStep(delta))
    }

    public func toggleMute() { enqueue(.toggleMute) }

    public func selectSource(_ sourceId: String) { enqueue(.source(sourceId)) }

    public func standby() { enqueue(.standby) }

    /// Wakes from standby, then switches to `sourceId` (muted, like any input
    /// switch) when it differs from the input the speaker woke up on.
    public func wake(to sourceId: String? = nil) { enqueue(.wake(sourceId)) }

    /// Play/pause, next or previous on the speaker's own player.
    public func sendPlayback(_ command: PlaybackCommand) { enqueue(.playback(command)) }

    public func clearNotice() { notice = nil }

    private func enqueue(_ intent: Intent) {
        guard driver != nil, phase == .online, let snapshot else {
            notice = loc("스피커에 연결된 뒤 다시 시도하세요", "Try again once the speaker is connected", "スピーカーに接続してからもう一度お試しください")
            return
        }
        // Only volume input is coalesced into the latest target. Other input
        // during a command is dropped, never deferred.
        if let current = queued {
            guard current.intent.isVolume, intent.isVolume else {
                notice = loc("이전 명령을 처리하는 중입니다", "Still working on the previous command", "前の操作を処理しています")
                return
            }
            queued = Job(intent: merge(current.intent, intent), expectedSource: current.expectedSource, token: current.token)
        } else if isBusy, !intent.isVolume {
            notice = loc("이전 명령을 처리하는 중입니다", "Still working on the previous command", "前の操作を処理しています")
            return
        } else {
            queued = Job(intent: intent, expectedSource: snapshot.sourceId, token: generation)
        }
        updatePendingVolume()
        if drainTask == nil {
            drainTask = Task {
                await drain()
                drainTask = nil
            }
        }
    }

    private func merge(_ old: Intent, _ new: Intent) -> Intent {
        switch (old, new) {
        case let (.volumeStep(a), .volumeStep(b)): .volumeStep(a + b)
        case let (.volume(a), .volumeStep(b)): .volume(VolumePolicy.clamp(a + b))
        // Once a queued command unmutes, the ones merged into it keep doing so.
        case let (.volumeUnmuting(a), .volumeStep(b)): .volumeUnmuting(VolumePolicy.clamp(a + b))
        case let (.volumeUnmuting, .volume(b)): .volumeUnmuting(b)
        default: new
        }
    }

    private func updatePendingVolume() {
        guard let job = queued else { return }
        switch job.intent {
        case let .volume(v): pendingVolume = v
        case let .volumeUnmuting(v):
            pendingVolume = v
            pendingUnmute = true
        case let .volumeStep(d): pendingVolume = VolumePolicy.clamp((pendingVolume ?? snapshot?.volume ?? 0) + d)
        default: break
        }
    }

    private func drain() async {
        guard !isBusy else { return }
        isBusy = true
        defer {
            isBusy = false
            activity = nil
            pendingVolume = nil
            pendingUnmute = false
            releaseHeldChanges()
        }
        while let job = queued {
            queued = nil
            activity = job.intent.activity
            await execute(job)
        }
    }

    private func execute(_ job: Job) async {
        guard job.token == generation, let driver, let device else { return }
        do {
            let before = try await read(driver, job.token)
            if case .wake = job.intent {
            } else {
                guard before.power == .on else { throw Stop(message: loc("스피커가 켜져 있지 않습니다", "The speaker isn't on", "スピーカーの電源が入っていません")) }
                guard before.sourceId == job.expectedSource else {
                    queued = nil
                    throw Stop(message: loc("외부에서 입력이 바뀌어 명령을 취소했습니다", "Cancelled: the input was changed elsewhere", "ほかの場所で入力が切り替えられたため、操作を取り消しました"))
                }
            }
            notice = nil
            let limit = effectiveLimit

            switch job.intent {
            case let .volume(requested):
                try await changeVolume(driver, job.token, before, requested: requested, limit: limit, device: device)
            case let .volumeUnmuting(requested):
                try await changeVolume(driver, job.token, before, requested: requested, limit: limit, device: device)
                try await unmuteAfterVolume(driver, job.token, limit: limit, device: device)
            case let .volumeStep(delta):
                try await changeVolume(driver, job.token, before, requested: before.volume + delta, limit: limit, device: device)
            case .toggleMute:
                guard device.capabilities.contains(.mute) else { throw Stop(message: loc("이 기기는 음소거를 지원하지 않습니다", "This device doesn't support mute", "この機器はミュートに対応していません")) }
                if before.muted, before.volume > limit {
                    throw Stop(message: loc("음량이 상한(\(limit))보다 높아 음소거 해제를 중지했습니다. 음량을 먼저 낮추세요", "Still muted: the volume is above the limit (\(limit)). Lower the volume first", "音量が上限（\(limit)）を超えているため、ミュートを解除しませんでした。先に音量を下げてください"))
                }
                try check(job.token)
                try await driver.setMute(!before.muted)
                _ = try await verify(driver, job.token) { $0.muted != before.muted && $0.sourceId == before.sourceId }
            case let .source(target):
                try await switchSource(driver, job.token, before, to: target, limit: limit, device: device)
            case .standby:
                guard device.capabilities.contains(.power) else { throw Stop(message: loc("이 기기는 앱에서 전원을 끌 수 없습니다", "This device can't be turned off from the app", "この機器はアプリから電源を切れません")) }
                try check(job.token)
                try await driver.setPower(false)
                _ = try await verify(driver, job.token, attempts: powerVerifyAttempts, delay: powerVerifyDelay, tolerateTransient: true) {
                    $0.power == .standby
                }
            case let .wake(target):
                try await wake(driver, job.token, before, to: target, limit: limit, device: device)
            case let .playback(command):
                try await play(driver, job.token, command, device: device)
            }
        } catch is Cancelled {
        } catch let stop as Stop {
            notice = stop.message
        } catch {
            goOffline(loc("제어 중지(자동 재시도 없음): \(describe(error))", "Stopped (no automatic retry): \(describe(error))", "操作を停止しました（自動再試行なし）: \(describe(error))"))
        }
    }

    /// The second half of `volumeUnmuting`: reads again and unmutes unless the
    /// volume is above the limit, the same rule as the mute button.
    private func unmuteAfterVolume(_ driver: any SpeakerDriver, _ token: Int, limit: Int, device: DeviceInfo) async throws {
        guard device.capabilities.contains(.mute) else { return }
        let now = try await read(driver, token)
        guard now.muted else { return }
        if now.volume > limit {
            throw Stop(message: loc("음량이 상한(\(limit))보다 높아 음소거 해제를 중지했습니다. 음량을 먼저 낮추세요", "Still muted: the volume is above the limit (\(limit)). Lower the volume first", "音量が上限（\(limit)）を超えているため、ミュートを解除しませんでした。先に音量を下げてください"))
        }
        try check(token)
        try await driver.setMute(false)
        _ = try await verify(driver, token) { !$0.muted && $0.sourceId == now.sourceId }
    }

    private func changeVolume(
        _ driver: any SpeakerDriver, _ token: Int, _ before: SpeakerSnapshot,
        requested: Int, limit: Int, device: DeviceInfo
    ) async throws {
        guard device.capabilities.contains(.volume) else { throw Stop(message: loc("이 기기는 음량 제어를 지원하지 않습니다", "This device doesn't support volume control", "この機器は音量の操作に対応していません")) }
        let target = VolumePolicy.target(requested: requested, current: before.volume, limit: limit)
        if VolumePolicy.clamp(requested) > target {
            notice = before.volume > limit ? loc("음량이 상한 \(limit)보다 높아 올리지 않았습니다", "Not raised: the volume is above the limit \(limit)", "音量が上限 \(limit) を超えているため上げませんでした") : loc("상한 \(limit)에 닿았습니다", "Reached the limit \(limit)", "上限 \(limit) に達しました")
        }
        guard target != before.volume else { return }
        if approachFromAbove, target > before.volume,
           let overshoot = driver.approachOvershoot(current: before.volume, target: target) {
            // A passing value: capped at the limit and never verified on its own.
            let passing = min(overshoot, limit)
            if passing > target {
                try check(token)
                try await driver.setVolume(passing)
                try await sleep(approachHold)
            }
        }
        try check(token)
        try await driver.setVolume(target)
        _ = try await verify(driver, token) { abs($0.volume - target) <= device.volumeTolerance && $0.sourceId == before.sourceId }
    }

    /// Mute, verify, switch, verify. Unmute again only when the new input is
    /// not louder than before and within the limit; otherwise stay muted.
    private func switchSource(
        _ driver: any SpeakerDriver, _ token: Int, _ before: SpeakerSnapshot,
        to target: String, limit: Int, device: DeviceInfo
    ) async throws {
        guard device.capabilities.contains(.source), device.sources.contains(where: { $0.id == target }) else {
            throw Stop(message: loc("지원하지 않는 입력입니다", "This input isn't supported", "対応していない入力です"))
        }
        guard before.sourceId != target else { return }
        guard before.volume <= limit else {
            throw Stop(message: loc("음량을 상한(\(limit)) 이하로 낮춘 뒤 전환하세요", "Lower the volume to the limit (\(limit)) or below before switching", "音量を上限（\(limit)）以下に下げてから切り替えてください"))
        }
        let wasMuted = before.muted
        if !wasMuted {
            try check(token)
            try await driver.setMute(true)
            _ = try await verify(driver, token) { $0.muted && $0.sourceId == before.sourceId }
        }
        try check(token)
        try await driver.setSource(target)
        let after = try await verify(driver, token) { $0.sourceId == target && $0.muted }
        guard !wasMuted else {
            notice = loc("입력을 바꿨습니다. 원래 음소거 상태를 유지합니다", "Input switched. It stays muted as before", "入力を切り替えました。ミュートは元のままです")
            return
        }
        guard after.volume <= before.volume, after.volume <= limit else {
            notice = loc("새 입력의 음량(\(after.volume))이 더 높아 음소거를 유지했습니다", "Kept muted: the new input's volume (\(after.volume)) is higher", "新しい入力の音量（\(after.volume)）のほうが高いため、ミュートのままにしました")
            return
        }
        try check(token)
        try await driver.setMute(false)
        _ = try await verify(driver, token) { !$0.muted && $0.sourceId == target }
    }

    /// Refused while the volume read in standby is above the limit, because the
    /// speaker wakes at that volume. Once on, the input switch is the usual
    /// muted one and is skipped when the woken volume is above the limit.
    private func wake(
        _ driver: any SpeakerDriver, _ token: Int, _ before: SpeakerSnapshot,
        to target: String?, limit: Int, device: DeviceInfo
    ) async throws {
        guard device.capabilities.contains(.power) else { throw Stop(message: loc("이 기기는 앱에서 전원을 켤 수 없습니다", "This device can't be turned on from the app", "この機器はアプリから電源を入れられません")) }
        if let target, !device.sources.contains(where: { $0.id == target }) {
            throw Stop(message: loc("지원하지 않는 입력입니다", "This input isn't supported", "対応していない入力です"))
        }
        var on = before
        if before.power != .on {
            guard before.volume <= limit else {
                throw Stop(message: loc("대기 전 음량(\(before.volume))이 상한(\(limit))보다 높아 켜지 않았습니다. 리모컨이나 KEF Connect로 음량을 낮춘 뒤 켜세요", "Not turned on: the volume before standby (\(before.volume)) is above the limit (\(limit)). Lower it with the remote or KEF Connect first", "スタンバイ前の音量（\(before.volume)）が上限（\(limit)）を超えているため、電源を入れませんでした。リモコンかKEF Connectで音量を下げてから入れてください"))
            }
            try check(token)
            lateWake = nil
            try await driver.setPower(true)
            let quickAttempts = wakeQuickAttempts, quick = powerVerifyDelay, slow = wakeSlowDelay
            do {
                on = try await verify(
                    driver, token, attempts: wakeAttempts, tolerateTransient: true,
                    delays: { $0 < quickAttempts ? quick : slow }
                ) { $0.power == .on && $0.sourceId != nil }
            } catch is VerificationError {
                // Every read worked, the speaker just isn't on yet. Not a
                // failure: it stays online, polling shows it as on once it
                // says so and replaces this notice then.
                var message = isWaking
                    ? loc("스피커가 아직 켜지는 중입니다. 켜지면 바로 표시합니다", "The speaker is still turning on. It shows as on as soon as it is", "スピーカーはまだ起動中です。電源が入るとすぐに表示されます")
                    : loc("스피커가 아직 켜지지 않았습니다. 켜지면 바로 표시합니다", "The speaker hasn't turned on yet. It shows as on as soon as it is", "スピーカーの電源はまだ入っていません。入るとすぐに表示されます")
                if target != nil {
                    message += loc(" 입력은 켜진 뒤 다시 고르세요", " Pick the input again once it's on", " 電源が入ってから入力をもう一度選んでください")
                }
                lateWake = (target, message)
                throw Stop(message: message)
            }
        }
        guard on.volume <= limit else {
            notice = loc("켜졌습니다. 음량(\(on.volume))이 상한(\(limit))보다 높으니 먼저 낮추세요", "Turned on. The volume (\(on.volume)) is above the limit (\(limit)); lower it first", "電源が入りました。音量（\(on.volume)）が上限（\(limit)）を超えているので、先に下げてください")
            return
        }
        if let target, device.capabilities.contains(.source) {
            try await switchSource(driver, token, on, to: target, limit: limit, device: device)
        }
    }

    // MARK: Now playing

    private func play(_ driver: any SpeakerDriver, _ token: Int, _ command: PlaybackCommand, device: DeviceInfo) async throws {
        guard device.capabilities.contains(.playback) else { throw Stop(message: UnsupportedOperation().description) }
        let shown = playback
        let shownPosition = playbackPositionMs()
        try check(token)
        do {
            try await driver.sendPlayback(command)
        } catch {
            // The player turned the command down, or the request was lost. Either way the
            // connection is left to the polls; a playback command never drops it.
            throw Stop(message: loc("스피커가 재생 명령을 받지 않았습니다: \(describe(error))", "The speaker didn't take the playback command: \(describe(error))", "スピーカーが再生コマンドを受け付けませんでした: \(describe(error))"))
        }
        try check(token)
        if command == .togglePause, shown.hasTrack {
            // Show the new state at once, like the system media controls; the reads below confirm it.
            var next = shown
            next.status = shown.status == .playing ? .paused : .playing
            next.positionMs = shownPosition
            setPlayback(next)
        }
        let epoch = driverEpoch
        Task { await confirmPlayback(driver, epoch, command, shown: shown, shownPosition: shownPosition) }
    }

    /// Reads the player until the command shows, without holding up other commands.
    private func confirmPlayback(_ driver: any SpeakerDriver, _ epoch: Int, _ command: PlaybackCommand, shown: PlaybackInfo, shownPosition: Int) async {
        playbackConfirming = true
        var last: PlaybackInfo?
        for _ in 0..<playbackConfirmAttempts {
            try? await sleep(playbackConfirmDelay)
            guard epoch == driverEpoch, let info = try? await driver.readPlayback(), epoch == driverEpoch else { break }
            last = info
            let done = switch command {
            case .togglePause: info.status != shown.status
            case .next, .previous:
                info.title != shown.title || info.artist != shown.artist
                    || (info.positionMs >= 0 && shownPosition >= 0 && info.positionMs + 1500 < shownPosition)
            }
            if done { break }
        }
        guard epoch == driverEpoch else { return }
        playbackConfirming = false
        if let last, playbackEnabled { setPlayback(last) }
        if playbackAgain { await refreshPlayback() }
    }

    /// Reads what is playing. Read-only; a failure keeps what is shown and is left to the polls.
    public func refreshPlayback() async {
        guard playbackEnabled, let driver, isOnline, device?.capabilities.contains(.playback) == true else { return }
        guard let snapshot, snapshot.power == .on, driver.sourceHasPlayback(snapshot.sourceId) else {
            setPlayback(.none)
            return
        }
        if playbackReading || playbackConfirming {
            playbackAgain = true
            return
        }
        playbackReading = true
        defer { playbackReading = false }
        let epoch = driverEpoch
        repeat {
            playbackAgain = false
            guard let info = try? await driver.readPlayback(), epoch == driverEpoch, playbackEnabled else { return }
            setPlayback(info)
        } while playbackAgain && !playbackConfirming
    }

    private func setPlayback(_ info: PlaybackInfo) {
        update(\.playback, info)
        playbackReadAt = uptime()
    }

    // MARK: Change reports

    private func startListening() {
        stopListening()
        guard listensForChanges, let driver, device?.capabilities.contains(.changes) == true else { return }
        let epoch = driverEpoch
        listenTask = Task { [weak self] in await self?.listen(driver, epoch) }
    }

    private func stopListening() {
        listenTask?.cancel()
        listenTask = nil
        update(\.changesActive, false)
    }

    /// Waits for change reports while online. Going offline, sleeping or a new
    /// driver cancels it; it starts again once the speaker is back.
    private func listen(_ driver: any SpeakerDriver, _ epoch: Int) async {
        var failed = 0
        while !Task.isCancelled, epoch == driverEpoch {
            let started = uptime()
            do {
                let changes = try await driver.waitForChanges()
                guard !Task.isCancelled, epoch == driverEpoch else { return }
                failed = 0
                update(\.changesActive, true)
                await applyChanges(changes)
                // A device that answers at once with nothing would otherwise be asked again in a tight loop.
                if !changes.any, uptime() - started < 1 { try await sleep(.seconds(1)) }
            } catch {
                guard !Task.isCancelled, epoch == driverEpoch else { return }
                failed += 1
                update(\.changesActive, false)
                // Find out at once whether the speaker is gone or only its change reports stopped.
                await refresh()
                do { try await sleep(listenBackoff[min(failed, listenBackoff.count) - 1]) } catch { return }
            }
        }
    }

    /// Reads only when the report says something the state doesn't show yet,
    /// so the echo of the app's own verified write costs nothing.
    private func applyChanges(_ changes: DeviceChanges) async {
        var changes = changes
        guard driver != nil, isOnline else { return }
        if isBusy {
            heldChanges = heldChanges?.merged(with: changes) ?? changes
            return
        }
        if changes.device {
            await refreshDevice()
            changes.device = false
        }
        let needsRead = changes.state || snapshot == nil
            || changes.volume.map { $0 != snapshot?.volume } == true
            || changes.muted.map { $0 != snapshot?.muted } == true
        if needsRead {
            if isBusy {
                heldChanges = heldChanges?.merged(with: changes) ?? changes
                return
            }
            await refresh()
        }
        if changes.playback { await refreshPlayback() }
    }

    private func releaseHeldChanges() {
        guard let held = heldChanges else { return }
        heldChanges = nil
        Task { await applyChanges(held) }
    }

    // MARK: Helpers

    private func read(_ driver: any SpeakerDriver, _ token: Int) async throws -> SpeakerSnapshot {
        let state = try await driver.read()
        try check(token)
        apply(state)
        stateEpoch += 1
        return state
    }

    /// `tolerateTransient` lets reads fail while the device changes power
    /// state; only the last attempt's error counts. `delays` gives the pause
    /// before each attempt after the first, by attempt index.
    private func verify(
        _ driver: any SpeakerDriver, _ token: Int,
        attempts: Int? = nil, delay: Duration? = nil, tolerateTransient: Bool = false,
        delays: ((Int) -> Duration)? = nil,
        _ predicate: (SpeakerSnapshot) -> Bool
    ) async throws -> SpeakerSnapshot {
        let attempts = attempts ?? verifyAttempts
        for attempt in 0..<attempts {
            if attempt > 0 { try await sleep(delays?(attempt) ?? delay ?? verifyDelay) }
            try check(token)
            do {
                let state = try await read(driver, token)
                if predicate(state) { return state }
            } catch let error where tolerateTransient && attempt < attempts - 1 && !(error is Cancelled) {
                continue
            }
        }
        throw VerificationError()
    }

    private func check(_ token: Int) throws {
        if token != generation { throw Cancelled() }
    }

    private func apply(_ state: SpeakerSnapshot) {
        let previous = snapshot
        update(\.snapshot, state)
        if playbackEnabled, previous?.sourceId != state.sourceId || previous?.power != state.power {
            Task { await refreshPlayback() }
        }
        if state.power == .on, let source = state.sourceId { update(\.lastActiveSource, source) }
        if state.power == .on, let wake = lateWake { finishLateWake(wake, state) }
    }

    /// The speaker came on after a wake stopped waiting for it. The notice
    /// from then no longer holds: it goes, or says the input it was asked for
    /// wasn't switched. Left alone if a newer notice replaced it.
    private func finishLateWake(_ wake: (source: String?, notice: String), _ state: SpeakerSnapshot) {
        lateWake = nil
        guard notice == wake.notice else { return }
        if let source = wake.source, source != state.sourceId {
            notice = loc("켜졌습니다. 늦게 켜져서 입력은 바꾸지 않았습니다", "Turned on. It took a while, so the input wasn't switched", "電源が入りました。時間がかかったため、入力は切り替えていません")
        } else {
            notice = nil
        }
    }

    /// Observation reports every assignment, equal or not. Polls read the same
    /// state again and again, so unchanged values are not assigned and the
    /// panel and the menu bar icon only draw again when something changed.
    private func update<Value: Equatable>(_ keyPath: ReferenceWritableKeyPath<SpeakerController, Value>, _ value: Value) {
        if self[keyPath: keyPath] != value { self[keyPath: keyPath] = value }
    }

    private func invalidate() {
        generation += 1
        queued = nil
        pendingVolume = nil
        pendingUnmute = false
    }

    private func goOffline(_ message: String) {
        invalidate()
        stopListening()
        failures = max(failures, 1)
        update(\.phase, .offline(message))
    }

    private func describe(_ error: any Error) -> String {
        if let error = error as? any CustomStringConvertible { return error.description }
        return String(describing: error)
    }
}

private let clockOrigin = ContinuousClock.now

/// Seconds on a monotonic clock, for counting the play position on.
func monotonicSeconds() -> Double {
    let elapsed = (ContinuousClock.now - clockOrigin).components
    return Double(elapsed.seconds) + Double(elapsed.attoseconds) / 1e18
}

public struct VerificationError: Error, CustomStringConvertible {
    public var description: String { loc("스피커 반영을 확인하지 못했습니다", "Couldn't confirm the speaker applied it", "スピーカーへの反映を確認できませんでした") }
}
