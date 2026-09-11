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

    /// <summary>Not produced by BuildCandidates/CooldownGate like the others — recorded directly by
    /// HealingEngine when Decision.EmergencyActionRateBreaker trips, i.e. Healer disabled itself
    /// because it took far more mutating actions than should ever be possible under normal
    /// cooldown-gated operation. Target is "host" (this is engine-wide, not tied to any one
    /// container/project).</summary>
    EmergencyStopTripped,
}

public enum ActionOutcomeStatus
{
    Success,
    Failed,
    SkippedCircuitOpen,
    SkippedCooldown,
    DryRun,

    /// <summary>"Would have done X, but Healer is currently disabled" — via the sentinel file
    /// (Abstractions.IEmergencyStopSignal), set by a human or by EmergencyActionRateBreaker tripping.
    /// Recorded to history every tick like everything else, but deliberately NOT sent to Telegram
    /// under ProblemsOnly (unlike DryRun): the operator already knows Healer is disabled — from the
    /// one-time trip alert, or from disabling it themselves — repeating that every 15 seconds is
    /// exactly the notification fatigue this whole system otherwise works to avoid.</summary>
    Disabled,
}
