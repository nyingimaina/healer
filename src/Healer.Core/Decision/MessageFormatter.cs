using Healer.Core.Models;

namespace Healer.Core.Decision;

/// <summary>All Telegram/log message wording lives here — INotifier implementations are transport-only, so wording is testable without a network call.</summary>
public static class MessageFormatter
{
    /// <summary>
    /// Prefixes a message with the server name, so a Telegram chat/bot shared across several boxes
    /// (a common setup) can tell at a glance which one an alert came from — without this, "container
    /// api is unhealthy" is ambiguous the moment there's more than one box.
    /// </summary>
    public static string WithServerPrefix(string serverName, string message) =>
        string.IsNullOrWhiteSpace(serverName) ? message : $"[{serverName}] {message}";

    public static string FormatIncident(Incident incident) =>
        $"[{incident.Severity}] {incident.Target}: {incident.Reason}";

    public static string FormatActionOutcome(ActionOutcome outcome)
    {
        var prefix = outcome.Status switch
        {
            ActionOutcomeStatus.DryRun => "DRY-RUN: would have",
            ActionOutcomeStatus.Success => "Done:",
            ActionOutcomeStatus.Failed => "FAILED:",
            ActionOutcomeStatus.SkippedCircuitOpen => "Skipped (repeated failures, alert-only):",
            ActionOutcomeStatus.SkippedCooldown => "Skipped (cooldown):",
            ActionOutcomeStatus.Disabled => "DISABLED: would have",
            _ => string.Empty,
        };

        var message = $"{prefix} {DescribeAction(outcome.Action)} — {outcome.Action.Reason}";

        if (outcome.Status == ActionOutcomeStatus.Failed && outcome.Detail is not null)
        {
            message += $" (error: {outcome.Detail})";
        }

        return message;
    }

    /// <summary>
    /// The one-time alert sent when Decision.EmergencyActionRateBreaker trips — deliberately answers
    /// "what went wrong" concretely (a breakdown of which actions/targets were actually attempted in
    /// the window), not just "something happened too often". The breakdown reuses DescribeAction so
    /// the wording matches every other action-outcome message in this system.
    /// </summary>
    public static string FormatEmergencyStopAlert(
        int actionsInWindow, int maxActionsInWindow, int windowMinutes, IReadOnlyList<RecentMutatingAction> recentActions)
    {
        var breakdown = recentActions
            .GroupBy(a => (a.Type, a.Target))
            .OrderByDescending(g => g.Count())
            .Select(g => $"  {g.Count()}x {DescribeAction(new PlannedAction { Type = g.Key.Type, Target = g.Key.Target, Reason = "" })}");

        return
            "🛑 Healer has disabled itself automatically.\n\n" +
            $"{actionsInWindow} actions in the last {windowMinutes} minutes (limit: {maxActionsInWindow}) — this should be " +
            "impossible under normal cooldown-gated operation and likely means a bug is bypassing Healer's own safety " +
            "throttles.\n\n" +
            "Recent actions:\n" + string.Join('\n', breakdown) + "\n\n" +
            "Healer will NOT take any further automatic action. Investigate (see healer-status's history for full " +
            "detail), then run 'sudo healer-enable' to resume.";
    }

    private static string DescribeAction(PlannedAction action) => action.Type switch
    {
        ActionType.ScheduledHostReboot => "reboot the host",
        ActionType.HostRebootVerification => "confirm the host reboot completed",
        ActionType.ScheduledContainerReboot => $"restart {action.Target} (scheduled)",
        ActionType.ScheduledComposeRestart => $"restart all containers in compose project '{action.Target}' (scheduled refresh)",
        ActionType.CrashLoopRestart => $"restart {action.Target} (crash loop)",
        ActionType.CriticalThresholdRestart => $"restart {action.Target} (memory critical)",
        ActionType.PreemptiveWorstOffenderRestart => $"restart {action.Target} (pre-emptive, worst memory offender)",
        ActionType.HostPressureRelief => "relieve host pressure (prune stopped containers/dangling images, ensure swap)",
        ActionType.EmergencyStopTripped => "disable itself (emergency stop)",
        _ => action.Target,
    };
}
