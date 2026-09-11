using Healer.Core.Decision;
using Healer.Core.Models;

namespace Healer.Tests;

public class EmergencyActionRateBreakerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private static RecentMutatingAction Action(DateTimeOffset at, string target = "web") =>
        new(at, ActionType.CrashLoopRestart, target);

    [Fact]
    public void RecordAndEvaluate_FirstAction_NeverTrips()
    {
        var (updated, shouldTrip) = EmergencyActionRateBreaker.RecordAndEvaluate([], Action(T0), Window, maxActionsInWindow: 15);

        Assert.False(shouldTrip);
        Assert.Single(updated);
    }

    [Fact]
    public void RecordAndEvaluate_ExactlyAtTheLimit_DoesNotTrip()
    {
        var recent = Enumerable.Range(0, 14).Select(i => Action(T0.AddSeconds(i))).ToList();

        var (updated, shouldTrip) = EmergencyActionRateBreaker.RecordAndEvaluate(recent, Action(T0.AddSeconds(14)), Window, maxActionsInWindow: 15);

        Assert.False(shouldTrip);
        Assert.Equal(15, updated.Count);
    }

    [Fact]
    public void RecordAndEvaluate_OneOverTheLimit_Trips()
    {
        var recent = Enumerable.Range(0, 15).Select(i => Action(T0.AddSeconds(i))).ToList();

        var (updated, shouldTrip) = EmergencyActionRateBreaker.RecordAndEvaluate(recent, Action(T0.AddSeconds(15)), Window, maxActionsInWindow: 15);

        Assert.True(shouldTrip);
        Assert.Equal(16, updated.Count);
    }

    [Fact]
    public void RecordAndEvaluate_PrunesEntriesOlderThanTheWindow_BeforeCounting()
    {
        // 20 actions, but all outside the window relative to the new one — must not trip.
        var recent = Enumerable.Range(0, 20).Select(i => Action(T0.AddSeconds(i))).ToList();
        var newAction = Action(T0 + Window + TimeSpan.FromMinutes(1));

        var (updated, shouldTrip) = EmergencyActionRateBreaker.RecordAndEvaluate(recent, newAction, Window, maxActionsInWindow: 15);

        Assert.False(shouldTrip);
        Assert.Single(updated);
    }

    [Fact]
    public void RecordAndEvaluate_MixOfStaleAndRecentEntries_OnlyCountsRecentOnesTowardTheLimit()
    {
        var farPast = T0 - TimeSpan.FromDays(1);
        var stale = Enumerable.Range(0, 30).Select(i => Action(farPast.AddSeconds(i))); // all far outside the window relative to newAction
        var recentWithinWindow = Enumerable.Range(0, 15).Select(i => Action(T0.AddSeconds(i)));
        var recent = stale.Concat(recentWithinWindow).ToList();

        var newAction = Action(T0.AddSeconds(15));
        var (updated, shouldTrip) = EmergencyActionRateBreaker.RecordAndEvaluate(recent, newAction, Window, maxActionsInWindow: 15);

        Assert.True(shouldTrip);
        Assert.Equal(16, updated.Count);
        Assert.DoesNotContain(updated, a => a.TimestampUtc < T0);
    }

    [Fact]
    public void RecordAndEvaluate_PreservesTypeAndTarget_ForTheAlertBreakdown()
    {
        var newAction = new RecentMutatingAction(T0, ActionType.HostPressureRelief, "host");

        var (updated, _) = EmergencyActionRateBreaker.RecordAndEvaluate([], newAction, Window, maxActionsInWindow: 15);

        Assert.Equal(ActionType.HostPressureRelief, updated[0].Type);
        Assert.Equal("host", updated[0].Target);
    }
}
