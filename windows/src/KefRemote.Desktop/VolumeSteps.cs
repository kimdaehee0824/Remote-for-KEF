namespace KefRemote.Desktop;

/// <summary>
/// Steps for volume keys that change loudness evenly instead of by a fixed number.
/// The table spreads 16 steps equally in measured dB between 12 and 65 (about 3.4 dB each), from the LSX II USB
/// measurements of the kef-lsx-volume project (MIT, see THIRD-PARTY-NOTICES.md). Below 12 the speaker is barely
/// audible, so the first step up goes straight to 12 and the last step down goes to 0.
/// </summary>
public static class VolumeSteps
{
    /// <summary>Step setting value meaning "follow <see cref="Table"/>".</summary>
    public const int Perceptual = 0;

    public static readonly IReadOnlyList<int> Table = [12, 15, 19, 22, 25, 29, 32, 36, 39, 42, 46, 49, 52, 56, 59, 65];

    /// <summary>Above the table the steps keep the size of its last interval.</summary>
    const int StepAboveTable = 4;

    public static int NextPerceptual(int current, int direction)
    {
        if (direction > 0)
        {
            foreach (int v in Table) if (v > current) return v;
            return Math.Min(100, current + StepAboveTable);
        }
        if (current > Table[^1]) return Math.Max(Table[^1], current - StepAboveTable);
        for (int i = Table.Count - 1; i >= 0; i--) if (Table[i] < current) return Table[i];
        return 0;
    }
}
