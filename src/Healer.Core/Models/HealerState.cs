using Healer.Core.Decision;

namespace Healer.Core.Models;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

/// <summary>Persisted backoff/circuit-breaker state for a single container, keyed by container name (not id).</summary>
public sealed class ContainerRuntimeState
{
    /// <summary>Index into the configured backoff stage list for the NEXT failure; -1 = no failures recorded yet (fresh). Reset to -1 on a verified successful restart.</summary>
    public int BackoffStageIndex { get; set; } = -1;

    /// <summary>A restart for this container is not attempted before this time.</summary>
    public DateTimeOffset? NextEligibleRestartUtc { get; set; }

    /// <summary>Timestamps (UTC) of failures within the rolling circuit-breaker window; pruned as they age out.</summary>
    public List<DateTimeOffset> FailureTimestampsUtc { get; set; } = [];

    public CircuitState CircuitState { get; set; } = CircuitState.Closed;

    public DateTimeOffset? CircuitOpenedAtUtc { get; set; }

    /// <summary>Set after a restart attempt; checked on a later tick to decide success vs. continued failure.</summary>
    public DateTimeOffset? PendingVerificationUntilUtc { get; set; }

    public DateTimeOffset? LastScheduledRebootUtc { get; set; }

    /// <summary>The container's RestartCount as of the previous tick, used to detect a sudden jump (crash loop) rather than requiring sustained unhealthy status.</summary>
    public int? LastObservedRestartCount { get; set; }
}

/// <summary>Capped-exponential-backoff notification throttle state for one scheduled action (keyed
/// by "ActionType:Target" in <see cref="HealerState.ScheduledSuccessNotify"/>) — see
/// <see cref="Decision.ScheduledSuccessNotificationGate"/>.</summary>
public sealed class ScheduledActionNotifyState
{
    public int SuccessesSinceLastNotify { get; set; }
    public int SkipThreshold { get; set; }
}

/// <summary>The full persisted state of the healing engine, surviving daemon restarts via <see cref="Abstractions.IStateStore"/>.</summary>
public sealed class HealerState
{
    public Dictionary<string, ContainerRuntimeState> Containers { get; set; } = [];

    /// <summary>Timestamp of the last mutating action taken of ANY kind, for the global cooldown gate.</summary>
    public DateTimeOffset? LastGlobalActionUtc { get; set; }

    public DateTimeOffset? LastScheduledHostRebootUtc { get; set; }

    /// <summary>Set right before a host reboot is actually triggered (scheduled, or a wizard "test
    /// reboot now"); checked every tick by <see cref="Decision.RebootVerifier"/> once the daemon
    /// restarts, since a reboot's own process never survives to report success itself. Cleared once
    /// resolved (verified or timed out), whichever comes first.</summary>
    public DateTimeOffset? PendingRebootRequestedUtc { get; set; }

    /// <summary>Free-text reason paired with <see cref="PendingRebootRequestedUtc"/>, e.g. "scheduled" or "wizard test" — surfaced in the verification's history/Telegram wording.</summary>
    public string? PendingRebootReason { get; set; }

    /// <summary>Last scheduled compose-restart run per project, keyed by <see cref="Configuration.ComposeProjectSchedule.ProjectName"/>.</summary>
    public Dictionary<string, DateTimeOffset> LastScheduledComposeRestartUtc { get; set; } = [];

    public DateTimeOffset? LastHistorySnapshotUtc { get; set; }

    public DateTimeOffset? LastRetentionSweepUtc { get; set; }

    /// <summary>Consecutive ticks host memory has been at/above the critical threshold — gates pre-emptive worst-offender selection so a single spike doesn't trigger it.</summary>
    public int HostMemoryCriticalStreak { get; set; }

    /// <summary>Per scheduled action, keyed "ActionType:Target" (e.g. "ScheduledComposeRestart:milele") — see <see cref="Decision.ScheduledSuccessNotificationGate"/>.</summary>
    public Dictionary<string, ScheduledActionNotifyState> ScheduledSuccessNotify { get; set; } = [];

    /// <summary>Rolling window of every mutating action Healer has attempted recently, regardless of
    /// type/target — see <see cref="Decision.EmergencyActionRateBreaker"/>. Pruned to the configured
    /// window each time a new one is recorded; never explicitly cleared (a genuinely quiet period
    /// ages entries out naturally on the next mutation, whenever that happens).</summary>
    public List<RecentMutatingAction> RecentMutatingActions { get; set; } = [];

    public ContainerRuntimeState GetOrAddContainer(string name)
    {
        if (!Containers.TryGetValue(name, out var state))
        {
            state = new ContainerRuntimeState();
            Containers[name] = state;
        }

        return state;
    }
}
