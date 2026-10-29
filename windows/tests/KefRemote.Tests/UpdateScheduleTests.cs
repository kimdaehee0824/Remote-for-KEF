using KefRemote.Desktop;
using Xunit;

namespace KefRemote.Tests;

/// <summary>Daily release checks counted from the saved last check, never right at startup.</summary>
public class UpdateScheduleTests
{
    static readonly DateTime Now = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstCheckWaitsForStartup() =>
        Assert.Equal(UpdateSchedule.AfterStart, UpdateSchedule.NextCheck(null, Now, TimeSpan.Zero));

    [Fact]
    public void OverdueCheckRunsOnceStartupIsOver()
    {
        Assert.Equal(TimeSpan.FromSeconds(35), UpdateSchedule.NextCheck(Now.AddDays(-3), Now, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.Zero, UpdateSchedule.NextCheck(Now.AddDays(-3), Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void RecentCheckWaitsForTheRestOfTheDay() =>
        Assert.Equal(TimeSpan.FromHours(4), UpdateSchedule.NextCheck(Now.AddHours(-20), Now, TimeSpan.Zero));

    [Fact]
    public void ClockSetBackWaitsOneIntervalAtMost() =>
        Assert.Equal(UpdateSchedule.Interval, UpdateSchedule.NextCheck(Now.AddDays(5), Now, TimeSpan.FromHours(1)));
}
