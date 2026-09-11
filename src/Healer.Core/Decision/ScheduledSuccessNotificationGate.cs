namespace Healer.Core.Decision;

/// <summary>
/// Capped exponential backoff on repeated-SUCCESS Telegram notifications for scheduled actions
/// (host reboots, compose restarts) — the mirror image of
/// <see cref="BackoffCircuitBreakerCalculator"/>, which backs off on repeated FAILURE to throttle
/// ACTIONS. This backs off on repeated success to throttle NOTIFICATIONS: a nightly schedule that
/// always succeeds would otherwise send an identical "it worked" message every single night forever,
/// training whoever reads the channel to stop reading it — exactly the failure mode
/// <see cref="Configuration.NotificationLevel.ProblemsOnly"/> already exists to avoid for routine
/// container restarts, just not (until now) for scheduled ones.
///
/// Count-based, not time-based: since these are all fixed-cadence schedules, "skip N occurrences"
/// and "skip N schedule cycles" are the same thing, so no wall-clock tracking is needed. Callers
/// track two ints per (ActionType, Target) key in <see cref="Models.HealerState.ScheduledSuccessNotify"/>
/// and reset them (see HealingEngine) the moment any non-success outcome occurs for that same key —
/// the concrete implementation of "reset the backoff on the first error message."
/// </summary>
public static class ScheduledSuccessNotificationGate
{
    public static (bool ShouldNotify, int SuccessesSinceLastNotify, int SkipThreshold) Evaluate(
        int successesSinceLastNotify, int skipThreshold, int maxSkipThreshold)
    {
        var successesSoFar = successesSinceLastNotify + 1;
        if (successesSoFar > skipThreshold)
        {
            var nextThreshold = skipThreshold == 0 ? 1 : Math.Min(maxSkipThreshold, skipThreshold * 2);
            return (true, 0, nextThreshold);
        }

        return (false, successesSoFar, skipThreshold);
    }
}
