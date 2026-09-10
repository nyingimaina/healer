using Healer.Core.Models;
using Healer.Status.Logic;

namespace Healer.Status.Tests;

public class ActionHistoryFormatterTests
{
    [Fact]
    public void FormatRow_IncludesTimestampTypeTargetAndOutcome()
    {
        var record = new ActionHistoryRecord
        {
            TimestampUtc = new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero),
            ActionType = ActionType.CrashLoopRestart,
            Target = "billing-api",
            TriggerReason = "unhealthy for 90s",
            DryRun = false,
            Outcome = ActionOutcomeStatus.Success,
        };

        var row = ActionHistoryFormatter.FormatRow(record);

        Assert.Contains("2026-01-01", row);
        Assert.Contains("CrashLoopRestart", row);
        Assert.Contains("billing-api", row);
        Assert.Contains("Success", row);
        Assert.DoesNotContain("DRY-RUN", row);
    }

    [Fact]
    public void FormatRow_FlagsDryRunEntries()
    {
        var record = new ActionHistoryRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ActionType = ActionType.HostPressureRelief,
            Target = "host",
            TriggerReason = "disk at 92%",
            DryRun = true,
            Outcome = ActionOutcomeStatus.DryRun,
        };

        var row = ActionHistoryFormatter.FormatRow(record);

        Assert.Contains("DRY-RUN", row);
    }

    [Fact]
    public void FormatLiveSummary_IncludesKeyMetricsAndContainerCounts()
    {
        var host = new HostMetrics
        {
            MemUsedPercent = 42,
            TotalMemoryBytes = 1_000_000,
            SwapUsedPercent = 5,
            LoadAvg1 = 1.2,
            LoadAvg5 = 1.0,
            LoadAvg15 = 0.8,
            CpuCoreCount = 4,
            DiskUsedPercentByMount = new Dictionary<string, double>(),
            BootTimeUtc = DateTimeOffset.UtcNow,
        };

        var summary = ActionHistoryFormatter.FormatLiveSummary(host, containerCount: 5, unhealthyCount: 1);

        Assert.Contains("42%", summary);
        Assert.Contains("5 container(s)", summary);
        Assert.Contains("1 unhealthy", summary);
    }
}
