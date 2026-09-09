namespace Healer.Core.Models;

/// <summary>A single row read back from the `actions` history table, for the Healer.Status incident browser.</summary>
public sealed record ActionHistoryRecord
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required ActionType ActionType { get; init; }
    public required string Target { get; init; }
    public required string TriggerReason { get; init; }
    public required bool DryRun { get; init; }
    public required ActionOutcomeStatus Outcome { get; init; }
    public string? Detail { get; init; }
}

/// <summary>A single (timestamp, value) sample for a Healer.Status trend sparkline.</summary>
public sealed record TrendPoint(DateTimeOffset TimestampUtc, double Value);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, bool HasMore);
