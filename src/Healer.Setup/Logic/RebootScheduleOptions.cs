using Healer.Core.Configuration;

namespace Healer.Setup.Logic;

/// <summary>
/// Backs the "how often should this reboot" screen: a handful of sensible presets the user picks
/// with no typing, plus a validated custom entry for anything else — the concrete implementation of
/// "support arbitrary intervals but present sensible options to pick from".
/// </summary>
public static class RebootScheduleOptions
{
    public static readonly (string Label, int Days)[] IntervalPresets =
    [
        ("Daily", 1),
        ("Weekly (recommended)", 7),
        ("Every 2 weeks", 14),
        ("Monthly (~30 days)", 30),
        ("Custom — enter number of days", -1), // -1 is the sentinel meaning "show the custom stepper"
    ];

    public static readonly (int Hour, string Label)[] HourPresets =
    [
        (0, "12:00 AM"),
        (1, "1:00 AM"),
        (2, "2:00 AM"),
        (3, "3:00 AM (recommended — typically the quietest hour)"),
        (4, "4:00 AM"),
    ];

    /// <summary>Finds which preset a saved interval corresponds to (for re-selecting it when the
    /// wizard re-runs on an already-configured box), falling back to the "Custom" sentinel entry for
    /// any interval that doesn't match one of the presets exactly.</summary>
    public static int ResolveIntervalPresetIndex(int days)
    {
        var index = Array.FindIndex(IntervalPresets, p => p.Days == days);
        return index >= 0 ? index : IntervalPresets.Length - 1;
    }

    /// <summary>Same idea as <see cref="ResolveIntervalPresetIndex"/> for the hour-of-day presets,
    /// falling back to the recommended 3 AM slot for an hour that isn't one of the presets.</summary>
    public static int ResolveHourPresetIndex(int hour)
    {
        var index = Array.FindIndex(HourPresets, p => p.Hour == hour);
        return index >= 0 ? index : 3;
    }

    public const int MinCustomIntervalDays = 1;
    public const int MaxCustomIntervalDays = 365;

    public static (bool IsValid, string? Reason) ValidateCustomIntervalDays(int days) =>
        days is >= MinCustomIntervalDays and <= MaxCustomIntervalDays
            ? (true, null)
            : (false, $"Enter a whole number of days between {MinCustomIntervalDays} and {MaxCustomIntervalDays}.");

    /// <summary>
    /// Builds the final schedule. For a weekly cadence (the common case), the anchor is computed
    /// from the chosen day-of-week so "every Sunday" behaves as expected; for any other interval,
    /// today (in the schedule's own timezone) is used as the anchor.
    /// </summary>
    public static RebootSchedule Build(int intervalDays, int hour, int minute, DayOfWeek? weeklyDayOfWeek, string timeZoneId, DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, ResolveTimeZone(timeZoneId));
        var today = DateOnly.FromDateTime(localNow.DateTime);

        var anchor = intervalDays == 7 && weeklyDayOfWeek is { } dayOfWeek
            ? NextOrTodayOccurrenceOf(today, dayOfWeek)
            : today;

        return new RebootSchedule
        {
            Enabled = true,
            IntervalDays = intervalDays,
            AnchorDate = anchor,
            Hour = hour,
            Minute = minute,
            TimeZoneId = timeZoneId,
        };
    }

    private static DateOnly NextOrTodayOccurrenceOf(DateOnly from, DayOfWeek dayOfWeek)
    {
        var daysUntil = ((int)dayOfWeek - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(daysUntil);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
