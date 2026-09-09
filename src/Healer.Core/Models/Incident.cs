namespace Healer.Core.Models;

public enum IncidentSeverity
{
    Warning,
    Critical,
}

/// <summary>A single detected condition (e.g. "container X memory at 96% of its limit"), independent of what (if anything) gets done about it.</summary>
public sealed record Incident
{
    public required ActionType SuggestedAction { get; init; }

    /// <summary>The container name this incident is about, or "host" for a host-wide incident.</summary>
    public required string Target { get; init; }

    public required IncidentSeverity Severity { get; init; }

    /// <summary>Plain-language reason, e.g. "memory 96% of 256MB limit". Used verbatim in notifications.</summary>
    public required string Reason { get; init; }
}

/// <summary>A candidate remediation action selected for possible execution this tick.</summary>
public sealed record PlannedAction
{
    public required ActionType Type { get; init; }

    public required string Target { get; init; }

    public required string Reason { get; init; }
}

/// <summary>The recorded result of attempting (or dry-running) a <see cref="PlannedAction"/>.</summary>
public sealed record ActionOutcome
{
    public required PlannedAction Action { get; init; }

    public required ActionOutcomeStatus Status { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Failure reason, or null on success/dry-run/skip.</summary>
    public string? Detail { get; init; }
}
