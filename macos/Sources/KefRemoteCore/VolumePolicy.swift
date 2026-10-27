public enum VolumePolicy {
    public static let defaultAppLimit = 30

    /// Lowering is always allowed. Raising stops at `limit`, and a volume that is
    /// already above the limit is never raised further.
    public static func target(requested: Int, current: Int, limit: Int) -> Int {
        let requested = clamp(requested)
        if requested <= current { return requested }
        if current >= limit { return current }
        return min(requested, limit)
    }

    public static func effectiveLimit(appLimit: Int, deviceLimit: Int?) -> Int {
        min(clamp(appLimit), clamp(deviceLimit ?? 100))
    }

    static func clamp(_ value: Int) -> Int { min(100, max(0, value)) }
}
