namespace Healer.Core.Models;

/// <summary>
/// The kind of remediation candidate produced by decision evaluation.
/// Ordinal order below IS the tie-break priority order used when merging
/// candidates in a single tick (lower ordinal = higher priority) — see
/// Engine.HealingEngine and Decision.CooldownGate.
/// </summary>
public enum ActionType
{
    ScheduledHostReboot,
    ScheduledContainerReboot,
    ScheduledComposeRestart,
    CrashLoopRestart,
    CriticalThresholdRestart,
    PreemptiveWorstOffenderRestart,
    HostPressureRelief,
}

public enum ActionOutcomeStatus
{
    Success,
    Failed,
    SkippedCircuitOpen,
    SkippedCooldown,
    DryRun,
}
