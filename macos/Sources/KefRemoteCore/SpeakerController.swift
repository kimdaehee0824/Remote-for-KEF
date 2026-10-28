import Observation

/// Serializes every read and write to one speaker and owns the safety rules
/// from docs/drivers.md:
/// - Connecting, reconnecting and polling only read. Nothing is written
///   unless the user asked for it.
/// - Every write is preceded by a fresh read and followed by read-back
///   verification. A write is never retried; an unverified result drops the
///   connection and reconnects read-only.
/// - A connection loss, an external input change or a settings change
///   invalidates queued commands. Nothing is replayed after reconnecting.
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
    }

    public private(set) var phase: Phase = .idle
    public private(set) var device: DeviceInfo?
    public private(set) var snapshot: SpeakerSnapshot?
    public private(set) var isBusy = false
    public private(set) var activity: Activity?
    /// Target of the volume command being processed, for display while the
    /// speaker has not confirmed it yet.
    public private(set) var pendingVolume: Int?
    /// Last user-facing message about a refused or stopped command.
    public private(set) var notice: String?
    /// Last input seen while powered on. Used to wake from standby.
    public private(set) var lastActiveSource: String?

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
    public var isAboveLimit: Bool { (snapshot?.volume ?? 0) > effectiveLimit }
    /// True when the KEF device maximum, not the app limit, is what stops the volume.
    public var limitFromDevice: Bool {
        guard let deviceLimit = device?.deviceVolumeLimit else { return false }
        return deviceLimit < VolumePolicy.clamp(appLimit)
    }

    private enum Intent: Equatable {
        case volume(Int)
        case volumeStep(Int)
        case toggleMute
        case source(String)
        case standby
        case wake(String?)

        var isVolume: Bool {
            switch self {
            case .volume, .volumeStep: true
            default: false
            }
        }

        var activity: Activity {
            switch self {
            case .volume, .volumeStep: .volume
            case .toggleMute: .mute
            case let .source(id): .source(id)
            case .standby: .powerOff
            case let .wake(id): .powerOn(id)
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

    private var driver: (any SpeakerDriver)?
    /// Bumped whenever earlier reads or intents must no longer be trusted.
    private var generation = 0
    /// Bumped whenever a command applies a read, so an older poll cannot
    /// overwrite it.
    private var stateEpoch = 0
    private var queued: Job?
    private var failures = 0
    private var suspended = false
    private var monitorTask: Task<Void, Never>?
    /// Internal so tests can wait for queued commands to finish.
    private(set) var drainTask: Task<Void, Never>?
    private let sleep: @Sendable (Duration) async throws -> Void

    let verifyAttempts = 4
    let verifyDelay: Duration = .milliseconds(180)
    let powerVerifyAttempts = 10
    let powerVerifyDelay: Duration = .milliseconds(500)
    /// How long the overshoot value is held before the target is written.
    let approachHold: Duration = .milliseconds(100)

    public init(sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }) {
        self.sleep = sleep
    }

    // MARK: Connection

    /// Replaces the driver and connects read-only.
    public func connect(_ driver: any SpeakerDriver) async {
        self.driver = driver
        device = nil
        snapshot = nil
        failures = 0
        phase = .connecting
        await reconnect()
    }

    public func disconnect() {
        invalidate()
        driver = nil
        device = nil
        snapshot = nil
        phase = .idle
    }

    /// For system sleep. Stops polling and drops queued commands.
    public func suspend() {
        suspended = true
        invalidate()
    }

    /// After wake, the state is re-read before anything else happens.
    public func resume() {
        suspended = false
        failures = 0
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
                notice = "외부에서 입력이 바뀌어 대기 중인 명령을 취소했습니다"
            }
            apply(state)
            failures = 0
            return true
        } catch {
            guard token == generation else { return false }
            goOffline("연결 확인 필요: \(describe(error))")
            return false
        }
    }

    /// Reads the device information again, for the KEF maximum volume that can
    /// change in KEF Connect at any time. Read-only; skipped while a command runs.
    public func refreshDevice() async {
        guard let driver, phase == .online, !isBusy else { return }
        let token = generation
        guard let info = try? await driver.connect(), token == generation else { return }
        device = info
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
            device = info
            apply(state)
            phase = .online
            failures = 0
            return true
        } catch {
            guard token == generation else { return false }
            failures += 1
            phase = .offline(describe(error))
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

    private func monitorStep() async -> Duration {
        guard driver != nil, !suspended else { return .seconds(1) }
        if isBusy { return .milliseconds(500) }
        if await refresh() {
            return isPanelVisible ? .milliseconds(1500) : .seconds(4)
        }
        // 2 → 5 → 10 → 30 seconds, without user-facing popups.
        let steps: [Duration] = [.seconds(2), .seconds(5), .seconds(10), .seconds(30)]
        return steps[min(max(failures, 1) - 1, steps.count - 1)]
    }

    // MARK: Commands

    public func setVolume(_ volume: Int) {
        enqueue(.volume(VolumePolicy.clamp(volume)))
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

    public func clearNotice() { notice = nil }

    private func enqueue(_ intent: Intent) {
        guard driver != nil, phase == .online, let snapshot else {
            notice = "스피커에 연결된 뒤 다시 시도하세요"
            return
        }
        // Only volume input is coalesced into the latest target. Other input
        // during a command is dropped, never deferred.
        if let current = queued {
            guard current.intent.isVolume, intent.isVolume else {
                notice = "이전 명령을 처리하는 중입니다"
                return
            }
            queued = Job(intent: merge(current.intent, intent), expectedSource: current.expectedSource, token: current.token)
        } else if isBusy, !intent.isVolume {
            notice = "이전 명령을 처리하는 중입니다"
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
        default: new
        }
    }

    private func updatePendingVolume() {
        guard let job = queued else { return }
        switch job.intent {
        case let .volume(v): pendingVolume = v
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
                guard before.power == .on else { throw Stop(message: "스피커가 켜져 있지 않습니다") }
                guard before.sourceId == job.expectedSource else {
                    queued = nil
                    throw Stop(message: "외부에서 입력이 바뀌어 명령을 취소했습니다")
                }
            }
            notice = nil
            let limit = effectiveLimit

            switch job.intent {
            case let .volume(requested):
                try await changeVolume(driver, job.token, before, requested: requested, limit: limit, device: device)
            case let .volumeStep(delta):
                try await changeVolume(driver, job.token, before, requested: before.volume + delta, limit: limit, device: device)
            case .toggleMute:
                guard device.capabilities.contains(.mute) else { throw Stop(message: "이 기기는 음소거를 지원하지 않습니다") }
                if before.muted, before.volume > limit {
                    throw Stop(message: "음량이 상한(\(limit))보다 높아 음소거 해제를 중지했습니다. 음량을 먼저 낮추세요")
                }
                try check(job.token)
                try await driver.setMute(!before.muted)
                _ = try await verify(driver, job.token) { $0.muted != before.muted && $0.sourceId == before.sourceId }
            case let .source(target):
                try await switchSource(driver, job.token, before, to: target, limit: limit, device: device)
            case .standby:
                guard device.capabilities.contains(.power) else { throw Stop(message: "이 기기는 앱에서 전원을 끌 수 없습니다") }
                try check(job.token)
                try await driver.setPower(false)
                _ = try await verify(driver, job.token, attempts: powerVerifyAttempts, delay: powerVerifyDelay, tolerateTransient: true) {
                    $0.power == .standby
                }
            case let .wake(target):
                try await wake(driver, job.token, before, to: target, limit: limit, device: device)
            }
        } catch is Cancelled {
        } catch let stop as Stop {
            notice = stop.message
        } catch {
            goOffline("제어 중지(자동 재시도 없음): \(describe(error))")
        }
    }

    private func changeVolume(
        _ driver: any SpeakerDriver, _ token: Int, _ before: SpeakerSnapshot,
        requested: Int, limit: Int, device: DeviceInfo
    ) async throws {
        guard device.capabilities.contains(.volume) else { throw Stop(message: "이 기기는 음량 제어를 지원하지 않습니다") }
        let target = VolumePolicy.target(requested: requested, current: before.volume, limit: limit)
        if VolumePolicy.clamp(requested) > target {
            notice = before.volume > limit ? "음량이 상한 \(limit)보다 높아 올리지 않았습니다" : "상한 \(limit)에 닿았습니다"
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
            throw Stop(message: "지원하지 않는 입력입니다")
        }
        guard before.sourceId != target else { return }
        guard before.volume <= limit else {
            throw Stop(message: "음량을 상한(\(limit)) 이하로 낮춘 뒤 전환하세요")
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
            notice = "입력을 바꿨습니다. 원래 음소거 상태를 유지합니다"
            return
        }
        guard after.volume <= before.volume, after.volume <= limit else {
            notice = "새 입력의 음량(\(after.volume))이 더 높아 음소거를 유지했습니다"
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
        guard device.capabilities.contains(.power) else { throw Stop(message: "이 기기는 앱에서 전원을 켤 수 없습니다") }
        if let target, !device.sources.contains(where: { $0.id == target }) {
            throw Stop(message: "지원하지 않는 입력입니다")
        }
        var on = before
        if before.power != .on {
            guard before.volume <= limit else {
                throw Stop(message: "대기 전 음량(\(before.volume))이 상한(\(limit))보다 높아 켜지 않았습니다. 리모컨이나 KEF Connect로 음량을 낮춘 뒤 켜세요")
            }
            try check(token)
            try await driver.setPower(true)
            on = try await verify(driver, token, attempts: powerVerifyAttempts, delay: powerVerifyDelay, tolerateTransient: true) {
                $0.power == .on && $0.sourceId != nil
            }
        }
        guard on.volume <= limit else {
            notice = "켜졌습니다. 음량(\(on.volume))이 상한(\(limit))보다 높으니 먼저 낮추세요"
            return
        }
        if let target, device.capabilities.contains(.source) {
            try await switchSource(driver, token, on, to: target, limit: limit, device: device)
        }
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
    /// state; only the last attempt's error counts.
    private func verify(
        _ driver: any SpeakerDriver, _ token: Int,
        attempts: Int? = nil, delay: Duration? = nil, tolerateTransient: Bool = false,
        _ predicate: (SpeakerSnapshot) -> Bool
    ) async throws -> SpeakerSnapshot {
        let attempts = attempts ?? verifyAttempts
        for attempt in 0..<attempts {
            if attempt > 0 { try await sleep(delay ?? verifyDelay) }
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
        snapshot = state
        if state.power == .on, let source = state.sourceId { lastActiveSource = source }
    }

    private func invalidate() {
        generation += 1
        queued = nil
        pendingVolume = nil
    }

    private func goOffline(_ message: String) {
        invalidate()
        failures = max(failures, 1)
        phase = .offline(message)
    }

    private func describe(_ error: any Error) -> String {
        if let error = error as? any CustomStringConvertible { return error.description }
        return String(describing: error)
    }
}

public struct VerificationError: Error, CustomStringConvertible {
    public var description: String { "스피커 반영을 확인하지 못했습니다" }
}
