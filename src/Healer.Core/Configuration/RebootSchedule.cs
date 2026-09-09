namespace Healer.Core.Configuration;

/// <summary>
/// A recurring reboot window defined by an arbitrary interval in days plus a local time of day —
/// not hardcoded to "weekly". A weekly schedule is just IntervalDays=7 anchored to the day it was
/// configured for; daily is IntervalDays=1; anything else (every 10 days, every 45 days, ...) works
/// the same way. This is what lets the setup wizard offer a handful of sensible presets (Daily /
/// Weekly / Every 2 weeks / Monthly) while still supporting any custom interval a user actually wants.
///
/// Positional record with constructor defaults — see the comment at the top of HealerConfig.cs for
/// why (System.Text.Json's source-generated deserializer silently drops `{ get; init; } = value`
/// initializers for properties missing from JSON; positional constructor defaults don't have that bug).
/// </summary>
public sealed record RebootSchedule(
    bool Enabled = false,
    int IntervalDays = 7,
    DateOnly AnchorDate = default,
    int Hour = 3,
    int Minute = 0,
    string? TimeZoneId = null)
{
    // These four have no sensible silent default (an "enabled" schedule missing its interval/hour
    // is a config mistake, not something to paper over) — `required` makes the source-generated
    // deserializer genuinely throw if a "host"/container schedule object omits any of them, the same
    // way it validates ServerName. Positional parameters alone would NOT throw here (they'd silently
    // take the CLR default), which is why each is redeclared as `required` in the body.
    public required bool Enabled { get; init; } = Enabled;
    public required int IntervalDays { get; init; } = IntervalDays;
    public required DateOnly AnchorDate { get; init; } = AnchorDate;
    public required int Hour { get; init; } = Hour;

    /// <summary>IANA timezone id (e.g. "Africa/Nairobi"). Defaults to the host's local timezone when not set.</summary>
    public string TimeZoneId { get; init; } = TimeZoneId ?? TimeZoneInfo.Local.Id;

    public static RebootSchedule Disabled(int intervalDays = 7, int hour = 3) => new()
    {
        Enabled = false,
        IntervalDays = Math.Max(1, intervalDays),
        AnchorDate = DateOnly.FromDateTime(DateTime.UtcNow),
        Hour = hour,
    };
}

public sealed record ContainerSchedule(string ContainerName = "", RebootSchedule? Schedule = null)
{
    public required string ContainerName { get; init; } = ContainerName;
    public required RebootSchedule Schedule { get; init; } = Schedule ?? RebootSchedule.Disabled();
}
