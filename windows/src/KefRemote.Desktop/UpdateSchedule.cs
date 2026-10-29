namespace KefRemote.Desktop;

/// <summary>
/// When to look for a new release: once a day, counted from the last check saved in the settings, so signing in
/// every morning does not mean a check every morning. Never right at startup, when sign-in is busy.
/// </summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan AfterStart = TimeSpan.FromSeconds(45);

    /// <param name="lastCheck">Last finished check in UTC, or null if there never was one.</param>
    /// <param name="sinceStart">Time since the app started.</param>
    public static TimeSpan NextCheck(DateTime? lastCheck, DateTime now, TimeSpan sinceStart)
    {
        TimeSpan due = lastCheck is DateTime last ? last + Interval - now : TimeSpan.Zero;
        // A clock set back would put the last check in the future; check after one interval at most.
        if (due > Interval) due = Interval;
        TimeSpan earliest = AfterStart - sinceStart;
        return due > earliest ? due : earliest > TimeSpan.Zero ? earliest : TimeSpan.Zero;
    }
}
