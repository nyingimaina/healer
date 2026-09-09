using Healer.Core.Configuration;
using Healer.Core.Decision;

namespace Healer.Tests;

public class ScheduleMatcherTests
{
    // 2026-01-04 is a Sunday (UTC).
    private static readonly DateOnly AnchorSunday = new(2026, 1, 4);

    private static RebootSchedule WeeklyAt3Am(string tz = "UTC") => new()
    {
        Enabled = true,
        IntervalDays = 7,
        AnchorDate = AnchorSunday,
        Hour = 3,
        Minute = 0,
        TimeZoneId = tz,
    };

    [Fact]
    public void ExactWindowMatch_OnScheduledDayAndHour_IsDue()
    {
        var schedule = WeeklyAt3Am();
        var now = new DateTimeOffset(2026, 1, 4, 3, 2, 0, TimeSpan.Zero);

        Assert.True(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, now));
    }

    [Fact]
    public void OutsideMatchWindow_IsNotDue()
    {
        var schedule = WeeklyAt3Am();
        var now = new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero);

        Assert.False(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, now));
    }

    [Fact]
    public void AlreadyRunInThisWindow_IsNotDueAgain()
    {
        var schedule = WeeklyAt3Am();
        var now = new DateTimeOffset(2026, 1, 4, 3, 2, 0, TimeSpan.Zero);
        var lastRun = new DateTimeOffset(2026, 1, 4, 3, 0, 30, TimeSpan.Zero);

        Assert.False(ScheduleMatcher.IsDue(schedule, lastRun, now));
    }

    [Fact]
    public void WrongDayOfWeek_ForWeeklyInterval_IsNotDue()
    {
        var schedule = WeeklyAt3Am();
        var now = new DateTimeOffset(2026, 1, 5, 3, 2, 0, TimeSpan.Zero); // Monday

        Assert.False(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, now));
    }

    [Fact]
    public void DailyInterval_FiresEveryDay()
    {
        var schedule = WeeklyAt3Am() with { IntervalDays = 1 };
        var monday = new DateTimeOffset(2026, 1, 5, 3, 2, 0, TimeSpan.Zero);

        Assert.True(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, monday));
    }

    [Fact]
    public void CustomIntervalDays_OnlyFiresOnMultiplesOfInterval()
    {
        var schedule = WeeklyAt3Am() with { IntervalDays = 10 };

        var day5 = new DateTimeOffset(2026, 1, 9, 3, 2, 0, TimeSpan.Zero); // 5 days after anchor
        var day10 = new DateTimeOffset(2026, 1, 14, 3, 2, 0, TimeSpan.Zero); // 10 days after anchor

        Assert.False(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, day5));
        Assert.True(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, day10));
    }

    [Fact]
    public void TimeZoneConversion_MapsCorrectlyToLocalHour()
    {
        // Africa/Nairobi is UTC+3, so 3 AM local is midnight UTC.
        var schedule = WeeklyAt3Am("Africa/Nairobi");
        var midnightUtc = new DateTimeOffset(2026, 1, 4, 0, 2, 0, TimeSpan.Zero);

        Assert.True(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, midnightUtc));
    }

    [Fact]
    public void MissedWindow_DoesNotFireLate()
    {
        var schedule = WeeklyAt3Am();
        // The daemon was down across the whole window; comes back hours later on the same day.
        var wayAfterWindow = new DateTimeOffset(2026, 1, 4, 14, 0, 0, TimeSpan.Zero);

        Assert.False(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, wayAfterWindow));
    }

    [Fact]
    public void DisabledSchedule_IsNeverDue()
    {
        var schedule = WeeklyAt3Am() with { Enabled = false };
        var now = new DateTimeOffset(2026, 1, 4, 3, 2, 0, TimeSpan.Zero);

        Assert.False(ScheduleMatcher.IsDue(schedule, lastRunUtc: null, now));
    }
}
