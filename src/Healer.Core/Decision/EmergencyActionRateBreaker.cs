using Healer.Core.Models;

namespace Healer.Core.Decision;

/// <summary>One mutating action Healer actually attempted (success or failure — a bug causing rapid
/// FAILED attempts is just as concerning as rapid successful ones), tracked for
/// <see cref="EmergencyActionRateBreaker"/>.</summary>
public sealed record RecentMutatingAction(DateTimeOffset TimestampUtc, ActionType Type, string Target);

/// <summary>
/// A last-resort, engine-wide circuit breaker — deliberately independent of the per-container
/// <see cref="BackoffCircuitBreakerCalculator"/> and the global <see cref="CooldownGate"/>, both of
/// which assume the decision logic driving them is correct. This one assumes nothing: it just counts
/// every mutating action attempted in a rolling window, regardless of type or target, and trips if
/// that count is higher than should ever be physically possible under a working `CooldownGate` —
/// e.g. with the default 90s global cooldown, ~10 actions/15min is already the theoretical ceiling,
/// so a default threshold of 15/15min can only be reached if the cooldown itself is broken. A true
/// tripwire for "something is bypassing Healer's own safety throttles," not a duplicate of them.
/// </summary>
public static class EmergencyActionRateBreaker
{
    public static (IReadOnlyList<RecentMutatingAction> UpdatedRecent, bool ShouldTrip) RecordAndEvaluate(
        IReadOnlyList<RecentMutatingAction> recent, RecentMutatingAction newAction, TimeSpan window, int maxActionsInWindow)
    {
        var updated = recent
            .Where(a => newAction.TimestampUtc - a.TimestampUtc <= window)
            .Append(newAction)
            .ToList();

        return (updated, updated.Count > maxActionsInWindow);
    }
}
