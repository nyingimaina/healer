using Healer.Core.Configuration;
using Healer.Core.Decision;
using Healer.Core.Models;

namespace Healer.Tests;

public class BackoffCircuitBreakerCalculatorTests
{
    private static readonly RestartPolicyConfig Policy = new()
    {
        BackoffStagesSeconds = [30, 60, 120, 300, 900],
        CircuitBreakerFailureThreshold = 5,
        CircuitBreakerWindowMinutes = 30,
        CircuitBreakerCooldownMinutes = 60,
        RestartVerificationGraceSeconds = 60,
        GlobalActionCooldownSeconds = 90,
        MaxConcurrentActionsPerTick = 1,
    };

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstFailure_UsesFirstBackoffStage()
    {
        var state = new ContainerRuntimeState();

        BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, T0);

        Assert.Equal(T0 + TimeSpan.FromSeconds(30), state.NextEligibleRestartUtc);
    }

    [Fact]
    public void RepeatedFailures_AdvanceStagesInOrder()
    {
        var state = new ContainerRuntimeState();
        var expected = new[] { 30, 60, 120, 300, 900 };
        var now = T0;

        foreach (var expectedSeconds in expected)
        {
            // Clear the rolling failure count each time so the circuit breaker (a separate concern,
            // covered by its own tests) doesn't trip mid-sequence and mask backoff-stage progression.
            state.FailureTimestampsUtc.Clear();

            BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, now);
            Assert.Equal(now + TimeSpan.FromSeconds(expectedSeconds), state.NextEligibleRestartUtc);
            now = state.NextEligibleRestartUtc!.Value; // advance past the wait before the next failure
        }
    }

    [Fact]
    public void FailuresBeyondLastStage_CapAtLastStage()
    {
        var state = new ContainerRuntimeState();
        var now = T0;

        for (var i = 0; i < 8; i++)
        {
            // Keep circuit closed for this test by resetting after each near-threshold count.
            if (state.FailureTimestampsUtc.Count >= Policy.CircuitBreakerFailureThreshold - 1)
            {
                state.FailureTimestampsUtc.Clear();
            }

            BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, now);
            now = state.NextEligibleRestartUtc ?? now;
        }

        Assert.Equal(900, Policy.BackoffStagesSeconds[state.BackoffStageIndex]);
    }

    [Fact]
    public void SuccessfulVerification_ResetsBackoffStage()
    {
        var state = new ContainerRuntimeState();
        BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, T0);
        BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, T0.AddMinutes(1));

        BackoffCircuitBreakerCalculator.RecordVerificationSuccess(state);

        Assert.Equal(-1, state.BackoffStageIndex);
        Assert.Null(state.NextEligibleRestartUtc);
        Assert.Null(state.PendingVerificationUntilUtc);
    }

    [Fact]
    public void RestartBeforeNextEligibleTime_IsRejected()
    {
        var state = new ContainerRuntimeState();
        BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, T0);

        var eligible = BackoffCircuitBreakerCalculator.IsEligibleForRestart(state, T0.AddSeconds(10));

        Assert.False(eligible);
    }

    [Fact]
    public void RestartAfterBackoffElapsed_IsAllowed()
    {
        var state = new ContainerRuntimeState();
        BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, T0);

        var eligible = BackoffCircuitBreakerCalculator.IsEligibleForRestart(state, T0.AddSeconds(31));

        Assert.True(eligible);
    }

    [Fact]
    public void FailureCountReachingThreshold_OpensCircuit()
    {
        var state = new ContainerRuntimeState();
        var now = T0;

        for (var i = 0; i < Policy.CircuitBreakerFailureThreshold; i++)
        {
            BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, now);
            now = now.AddMinutes(1);
        }

        Assert.Equal(CircuitState.Open, state.CircuitState);
    }

    [Fact]
    public void OpenCircuit_BlocksRestartRegardlessOfBackoffStage()
    {
        var state = new ContainerRuntimeState { CircuitState = CircuitState.Open, CircuitOpenedAtUtc = T0 };

        var eligible = BackoffCircuitBreakerCalculator.IsEligibleForRestart(state, T0.AddSeconds(1));

        Assert.False(eligible);
    }

    [Fact]
    public void OpenCircuit_TransitionsToHalfOpen_AfterCooldownElapses()
    {
        var state = new ContainerRuntimeState { CircuitState = CircuitState.Open, CircuitOpenedAtUtc = T0 };

        BackoffCircuitBreakerCalculator.UpdateCircuitTransition(state, Policy, T0.AddMinutes(Policy.CircuitBreakerCooldownMinutes));

        Assert.Equal(CircuitState.HalfOpen, state.CircuitState);
    }

    [Fact]
    public void OpenCircuit_StaysOpen_BeforeCooldownElapses()
    {
        var state = new ContainerRuntimeState { CircuitState = CircuitState.Open, CircuitOpenedAtUtc = T0 };

        BackoffCircuitBreakerCalculator.UpdateCircuitTransition(state, Policy, T0.AddMinutes(Policy.CircuitBreakerCooldownMinutes - 1));

        Assert.Equal(CircuitState.Open, state.CircuitState);
    }

    [Fact]
    public void FailedHalfOpenTrial_ReOpensTheCircuit()
    {
        var state = new ContainerRuntimeState { CircuitState = CircuitState.HalfOpen };

        BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, T0);

        Assert.Equal(CircuitState.Open, state.CircuitState);
        Assert.Equal(T0, state.CircuitOpenedAtUtc);
    }

    [Fact]
    public void SuccessfulHalfOpenTrial_ClosesTheCircuit()
    {
        var state = new ContainerRuntimeState { CircuitState = CircuitState.HalfOpen, FailureTimestampsUtc = [T0] };

        BackoffCircuitBreakerCalculator.RecordVerificationSuccess(state);

        Assert.Equal(CircuitState.Closed, state.CircuitState);
        Assert.Empty(state.FailureTimestampsUtc);
    }

    [Fact]
    public void FailuresOutsideRollingWindow_DoNotCountTowardThreshold()
    {
        var state = new ContainerRuntimeState();

        // One old failure, well outside the 30-minute window.
        BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, T0);

        // Four more failures, close together, long after the window has passed for the first one.
        var now = T0.AddHours(2);
        for (var i = 0; i < 4; i++)
        {
            BackoffCircuitBreakerCalculator.RecordVerificationFailure(state, Policy, now);
            now = now.AddMinutes(1);
        }

        Assert.Equal(CircuitState.Closed, state.CircuitState);
        Assert.Equal(4, state.FailureTimestampsUtc.Count);
    }

    [Fact]
    public void TwoContainers_MaintainIndependentState()
    {
        var stateA = new ContainerRuntimeState();
        var stateB = new ContainerRuntimeState();

        BackoffCircuitBreakerCalculator.RecordVerificationFailure(stateA, Policy, T0);

        Assert.NotNull(stateA.NextEligibleRestartUtc);
        Assert.Null(stateB.NextEligibleRestartUtc);
        Assert.Equal(-1, stateB.BackoffStageIndex);
    }

    [Fact]
    public void PendingVerification_BlocksAnotherRestartUntilResolved()
    {
        var state = new ContainerRuntimeState();
        BackoffCircuitBreakerCalculator.RecordRestartAttempt(state, Policy, T0);

        var eligible = BackoffCircuitBreakerCalculator.IsEligibleForRestart(state, T0.AddSeconds(1));

        Assert.False(eligible);
    }
}
