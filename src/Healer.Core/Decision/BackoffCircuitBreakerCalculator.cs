using Healer.Core.Configuration;
using Healer.Core.Models;

namespace Healer.Core.Decision;

/// <summary>
/// Pure, deterministic per-container backoff and circuit-breaker logic, driven entirely by
/// the caller's supplied `now` (never DateTime.UtcNow directly) so it's exercised in tests via
/// Microsoft.Extensions.TimeProvider.Testing. Mutates the persisted ContainerRuntimeState it's
/// given — HealingEngine is responsible for saving that state afterward.
/// </summary>
public static class BackoffCircuitBreakerCalculator
{
    /// <summary>Whether a restart may be attempted right now: circuit must be closed or half-open, no restart already pending verification, and any backoff wait must have elapsed.</summary>
    public static bool IsEligibleForRestart(ContainerRuntimeState state, DateTimeOffset now)
    {
        if (state.CircuitState == CircuitState.Open)
        {
            return false;
        }

        if (state.PendingVerificationUntilUtc is { } pending && now < pending)
        {
            return false;
        }

        if (state.NextEligibleRestartUtc is { } next && now < next)
        {
            return false;
        }

        return true;
    }

    /// <summary>Call once per tick, before checking eligibility, so an Open circuit whose cooldown has elapsed flips to HalfOpen (permitting exactly one trial restart).</summary>
    public static void UpdateCircuitTransition(ContainerRuntimeState state, RestartPolicyConfig policy, DateTimeOffset now)
    {
        if (state.CircuitState == CircuitState.Open
            && state.CircuitOpenedAtUtc is { } openedAt
            && now - openedAt >= TimeSpan.FromMinutes(policy.CircuitBreakerCooldownMinutes))
        {
            state.CircuitState = CircuitState.HalfOpen;
        }
    }

    /// <summary>Call immediately after issuing a restart, to schedule the later verification check.</summary>
    public static void RecordRestartAttempt(ContainerRuntimeState state, RestartPolicyConfig policy, DateTimeOffset now)
    {
        state.PendingVerificationUntilUtc = now + TimeSpan.FromSeconds(policy.RestartVerificationGraceSeconds);
    }

    /// <summary>Call when a previously-attempted restart is confirmed stable: clears backoff/pending state and closes a half-open circuit.</summary>
    public static void RecordVerificationSuccess(ContainerRuntimeState state)
    {
        state.BackoffStageIndex = -1;
        state.NextEligibleRestartUtc = null;
        state.PendingVerificationUntilUtc = null;

        if (state.CircuitState == CircuitState.HalfOpen)
        {
            state.CircuitState = CircuitState.Closed;
            state.CircuitOpenedAtUtc = null;
            state.FailureTimestampsUtc.Clear();
        }
    }

    /// <summary>Call when a previously-attempted restart is confirmed to have NOT resolved the problem: advances backoff, and opens/re-opens the circuit as appropriate.</summary>
    public static void RecordVerificationFailure(ContainerRuntimeState state, RestartPolicyConfig policy, DateTimeOffset now)
    {
        state.PendingVerificationUntilUtc = null;

        var windowStart = now - TimeSpan.FromMinutes(policy.CircuitBreakerWindowMinutes);
        state.FailureTimestampsUtc.RemoveAll(t => t < windowStart);
        state.FailureTimestampsUtc.Add(now);

        if (state.CircuitState == CircuitState.HalfOpen)
        {
            // The one trial restart failed: re-open and restart the cooldown clock.
            state.CircuitState = CircuitState.Open;
            state.CircuitOpenedAtUtc = now;
            return;
        }

        if (state.FailureTimestampsUtc.Count >= policy.CircuitBreakerFailureThreshold)
        {
            state.CircuitState = CircuitState.Open;
            state.CircuitOpenedAtUtc = now;
            return;
        }

        var stages = policy.BackoffStagesSeconds;
        state.BackoffStageIndex = Math.Min(state.BackoffStageIndex + 1, stages.Count - 1);
        state.NextEligibleRestartUtc = now + TimeSpan.FromSeconds(stages[state.BackoffStageIndex]);
    }
}
