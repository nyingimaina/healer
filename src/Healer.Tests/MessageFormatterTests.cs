using Healer.Core.Decision;
using Healer.Core.Models;

namespace Healer.Tests;

public class MessageFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IncidentMessage_IncludesContainerNameAndTriggeringValue()
    {
        var incident = new Incident
        {
            SuggestedAction = ActionType.CriticalThresholdRestart,
            Target = "billing-api",
            Severity = IncidentSeverity.Critical,
            Reason = "memory at 96% of its 256MB limit",
        };

        var message = MessageFormatter.FormatIncident(incident);

        Assert.Contains("billing-api", message);
        Assert.Contains("96%", message);
    }

    [Fact]
    public void ActionOutcomeMessage_DryRun_IsClearlyDistinguishedFromLive()
    {
        var action = new PlannedAction { Type = ActionType.CrashLoopRestart, Target = "app", Reason = "unhealthy for 90s" };

        var dryRunMessage = MessageFormatter.FormatActionOutcome(new ActionOutcome { Action = action, Status = ActionOutcomeStatus.DryRun, TimestampUtc = Now });
        var liveMessage = MessageFormatter.FormatActionOutcome(new ActionOutcome { Action = action, Status = ActionOutcomeStatus.Success, TimestampUtc = Now });

        Assert.Contains("DRY-RUN", dryRunMessage);
        Assert.DoesNotContain("DRY-RUN", liveMessage);
        Assert.NotEqual(dryRunMessage, liveMessage);
    }

    [Fact]
    public void WithServerPrefix_PrependsServerNameInBrackets()
    {
        var result = MessageFormatter.WithServerPrefix("prod-api-1", "container app is unhealthy");

        Assert.Equal("[prod-api-1] container app is unhealthy", result);
    }

    [Fact]
    public void WithServerPrefix_LeavesMessageUnchanged_WhenServerNameIsBlank()
    {
        var result = MessageFormatter.WithServerPrefix("", "container app is unhealthy");

        Assert.Equal("container app is unhealthy", result);
    }

    [Fact]
    public void ActionOutcomeMessage_Failure_IncludesFailureReason()
    {
        var action = new PlannedAction { Type = ActionType.HostPressureRelief, Target = "host", Reason = "disk at 92%" };
        var outcome = new ActionOutcome { Action = action, Status = ActionOutcomeStatus.Failed, TimestampUtc = Now, Detail = "permission denied" };

        var message = MessageFormatter.FormatActionOutcome(outcome);

        Assert.Contains("FAILED", message);
        Assert.Contains("permission denied", message);
    }
}
