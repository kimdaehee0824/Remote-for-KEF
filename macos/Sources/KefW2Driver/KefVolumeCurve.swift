/// Volume behaviour measured on an LSX II over USB by the kef-lsx-volume
/// project (MIT, see THIRD-PARTY-NOTICES.md). The same `player:volume` value
/// sounds quieter when it is reached from below than from above, by up to
/// 10 dB around 12 and not at all from 41 up. Writing a slightly higher value
/// for a moment first makes every target sound as if it was reached from
/// above. Not yet measured on the LSX II LT.
public enum KefVolumeCurve {
    // Smallest value whose from-below level is at least the from-above level of
    // the target (0.2 dB margin). Index = target volume; 0 means "write directly".
    // Same table as the Windows KefVolumeCurve.
    static let overshootTable: [Int] = [
        0, 0, 0, 0, 0, 0, 14, 15, 15, 15,
        16, 17, 17, 18, 18, 19, 20, 20, 21, 22,
        22, 23, 24, 25, 26, 27, 28, 28, 29, 30,
        31, 32, 33, 34, 35, 36, 37, 38, 39, 40,
        41,
    ]

    /// The value to pass through before raising to `target`, or nil to write it directly.
    public static func overshoot(for target: Int) -> Int? {
        guard overshootTable.indices.contains(target) else { return nil }
        let value = overshootTable[target]
        return value > target ? value : nil
    }
}
