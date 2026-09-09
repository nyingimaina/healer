using Healer.Core.Configuration;
using Healer.Core.Decision;
using Healer.Core.Models;

namespace Healer.Tests;

public class CooldownGateTests
{
    private static readonly RestartPolicyConfig Policy = new() { GlobalActionCooldownSeconds = 90, MaxConcurrentActionsPerTick = 1 };
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlannedAction Action(ActionType type, string target = "x") => new() { Type = type, Target = target, Reason = "test" };

    [Fact]
    public void NoPriorAction_ImmediateActionIsAllowed()
    {
        var chosen = CooldownGate.SelectActions([Action(ActionType.CrashLoopRestart)], Policy, lastGlobalActionUtc: null, Now);

        Assert.Single(chosen);
    }

    [Fact]
    public void WithinCooldownWindow_ActionIsBlocked()
    {
        var lastAction = Now.AddSeconds(-10);

        var chosen = CooldownGate.SelectActions([Action(ActionType.CrashLoopRestart)], Policy, lastAction, Now);

        Assert.Empty(chosen);
    }

    [Fact]
    public void AfterCooldownElapses_ActionIsAllowed()
    {
        var lastAction = Now.AddSeconds(-91);

        var chosen = CooldownGate.SelectActions([Action(ActionType.CrashLoopRestart)], Policy, lastAction, Now);

        Assert.Single(chosen);
    }

    [Fact]
    public void MultipleCandidates_OnlyHighestPriorityChosen()
    {
        var candidates = new List<PlannedAction>
        {
            Action(ActionType.HostPressureRelief, "host"),
            Action(ActionType.ScheduledHostReboot, "host"),
        };

        var chosen = CooldownGate.SelectActions(candidates, Policy, lastGlobalActionUtc: null, Now);

        Assert.Equal(ActionType.ScheduledHostReboot, Assert.Single(chosen).Type);
    }

    [Fact]
    public void PriorityOrdering_ScheduledRebootBeatsPreemptiveWorstOffender()
    {
        var candidates = new List<PlannedAction>
        {
            Action(ActionType.PreemptiveWorstOffenderRestart, "a"),
            Action(ActionType.ScheduledHostReboot, "host"),
        };

        var chosen = CooldownGate.SelectActions(candidates, Policy, lastGlobalActionUtc: null, Now);

        Assert.Equal(ActionType.ScheduledHostReboot, Assert.Single(chosen).Type);
    }

    [Fact]
    public void NeverReturnsMoreThanMaxConcurrentActionsPerTick()
    {
        var policy = Policy with { MaxConcurrentActionsPerTick = 2 };
        var candidates = new List<PlannedAction>
        {
            Action(ActionType.CrashLoopRestart, "a"),
            Action(ActionType.CrashLoopRestart, "b"),
            Action(ActionType.CrashLoopRestart, "c"),
        };

        var chosen = CooldownGate.SelectActions(candidates, policy, lastGlobalActionUtc: null, Now);

        Assert.Equal(2, chosen.Count);
    }
}
