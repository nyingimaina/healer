using Healer.Core.Configuration;

namespace Healer.Core.Decision;

/// <summary>
/// Matches an interval-based reboot schedule against "now", entirely in the schedule's own
/// timezone so nothing about DST or UTC offsets needs to be understood by whoever configured it.
///
/// Catch-up policy (explicit choice, not accidental): a window that was missed entirely — e.g.
/// the daemon itself was down across the whole match window — is simply skipped, not fired late
/// once the daemon comes back. A host reboot firing hours late, possibly during business hours,
/// would defeat the point of scheduling it for a quiet window in the first place.
/// </summary>
public static class ScheduleMatcher
{
    public static readonly TimeSpan DefaultMatchWindow = TimeSpan.FromMinutes(5);

    public static bool IsDue(RebootSchedule schedule, DateTimeOffset? lastRunUtc, DateTimeOffset nowUtc, TimeSpan? matchWindow = null)
    {
        if (!schedule.Enabled || schedule.IntervalDays < 1)
        {
            return false;
        }

        var window = matchWindow ?? DefaultMatchWindow;
        var timeZone = ResolveTimeZone(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localToday = DateOnly.FromDateTime(localNow.DateTime);

        var daysSinceAnchor = localToday.DayNumber - schedule.AnchorDate.DayNumber;
        if (daysSinceAnchor < 0 || daysSinceAnchor % schedule.IntervalDays != 0)
        {
            return false;
        }

        var scheduledLocal = new DateTimeOffset(localToday.Year, localToday.Month, localToday.Day, schedule.Hour, schedule.Minute, 0, localNow.Offset);
        var sinceScheduled = localNow - scheduledLocal;

        if (sinceScheduled < TimeSpan.Zero || sinceScheduled > window)
        {
            return false;
        }

        // Idempotency: don't fire twice within the same window if it already ran once this occurrence.
        if (lastRunUtc is { } last && last >= scheduledLocal.ToUniversalTime())
        {
            return false;
        }

        return true;
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
