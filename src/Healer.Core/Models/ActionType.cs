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

    /// <summary>Not produced by BuildCandidates/CooldownGate like the others — recorded directly by
    /// HealingEngine.MaybeVerifyPendingRebootAsync once a pending reboot (scheduled, or a wizard
    /// "test reboot now") resolves, since a reboot's own process never survives to report itself.</summary>
    HostRebootVerification,
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
