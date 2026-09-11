using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class RebootScheduleOptionsTests
{
    [Theory]
    [InlineData(1, true)]    // minimum boundary
    [InlineData(100, true)]  // ordinary value
    [InlineData(365, true)]  // maximum boundary
    [InlineData(0, false)]   // one below minimum
    [InlineData(366, false)] // one above maximum
    [InlineData(-5, false)]  // negative
    public void ValidateCustomIntervalDays_EnforcesBounds(int days, bool expectedValid)
    {
        var (valid, reason) = RebootScheduleOptions.ValidateCustomIntervalDays(days);

        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedValid, reason is null);
    }

    [Fact]
    public void Build_WeeklyInterval_AnchorsToChosenDayOfWeek()
    {
        // 2026-01-05 is a Monday (UTC).
        var now = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

        var schedule = RebootScheduleOptions.Build(intervalDays: 7, hour: 3, minute: 0, weeklyDayOfWeek: DayOfWeek.Sunday, timeZoneId: "UTC", nowUtc: now);

        Assert.Equal(DayOfWeek.Sunday, schedule.AnchorDate.DayOfWeek);
        Assert.True(schedule.Enabled);
    }

    [Fact]
    public void Build_NonWeeklyInterval_AnchorsToToday()
    {
        var now = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

        var schedule = RebootScheduleOptions.Build(intervalDays: 3, hour: 3, minute: 0, weeklyDayOfWeek: null, timeZoneId: "UTC", nowUtc: now);

        Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime), schedule.AnchorDate);
        Assert.Equal(3, schedule.IntervalDays);
    }

    [Fact]
    public void IntervalPresets_LastEntryIsTheCustomSentinel()
    {
        var last = RebootScheduleOptions.IntervalPresets[^1];

        Assert.Equal(-1, last.Days);
        Assert.Contains("Custom", last.Label);
    }

    [Theory]
    [InlineData(1, 0)]  // Daily
    [InlineData(7, 1)]  // Weekly
    [InlineData(14, 2)] // Every 2 weeks
    [InlineData(30, 3)] // Monthly
    public void ResolveIntervalPresetIndex_MatchesAnExactPreset(int days, int expectedIndex)
    {
        Assert.Equal(expectedIndex, RebootScheduleOptions.ResolveIntervalPresetIndex(days));
    }

    [Fact]
    public void ResolveIntervalPresetIndex_FallsBackToCustomForAnUnmatchedInterval()
    {
        var index = RebootScheduleOptions.ResolveIntervalPresetIndex(10);

        Assert.Equal(RebootScheduleOptions.IntervalPresets.Length - 1, index);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    public void ResolveHourPresetIndex_MatchesAnExactPreset(int hour, int expectedIndex)
    {
        Assert.Equal(expectedIndex, RebootScheduleOptions.ResolveHourPresetIndex(hour));
    }

    [Fact]
    public void ResolveHourPresetIndex_FallsBackToTheRecommendedDefaultForAnUnmatchedHour()
    {
        Assert.Equal(3, RebootScheduleOptions.ResolveHourPresetIndex(17));
    }
}
