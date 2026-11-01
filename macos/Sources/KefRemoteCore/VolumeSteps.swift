/// How far one press of a volume shortcut moves: a fixed number, or steps of even
/// loudness. Same rules and table as the Windows `VolumeSteps` and
/// `VolumeKeyAccumulator.NextTarget`.
public enum VolumeSteps {
    /// Step setting meaning "follow `table`".
    public static let perceptual = 0
    /// The step choices offered in settings, `perceptual` last.
    public static let choices = [1, 2, 3, 5, perceptual]

    /// 16 steps spread equally in measured dB between 12 and 65 (about 3.4 dB each),
    /// from the LSX II USB measurements of the kef-lsx-volume project (MIT, see
    /// THIRD-PARTY-NOTICES.md). Below 12 the speaker is barely audible, so the first
    /// step up goes straight to 12 and the last step down goes to 0.
    public static let table = [12, 15, 19, 22, 25, 29, 32, 36, 39, 42, 46, 49, 52, 56, 59, 65]

    /// Above the table the steps keep the size of its last interval.
    static let stepAboveTable = 4

    public static func nextPerceptual(_ current: Int, direction: Int) -> Int {
        if direction > 0 {
            return table.first { $0 > current } ?? min(100, current + stepAboveTable)
        }
        if let last = table.last, current > last { return max(last, current - stepAboveTable) }
        return table.last { $0 < current } ?? 0
    }

    /// The volume one press goes to. Up never passes the limit, and a volume already
    /// above it is not raised; down always works and stops at 0.
    public static func next(current: Int, direction: Int, step: Int, limit: Int) -> Int {
        let current = VolumePolicy.clamp(current)
        let limit = VolumePolicy.clamp(limit)
        let next = step == perceptual
            ? nextPerceptual(current, direction: direction)
            : current + direction * min(10, max(1, step))
        if direction > 0 { return current >= limit ? current : min(limit, next) }
        return max(0, next)
    }
}
