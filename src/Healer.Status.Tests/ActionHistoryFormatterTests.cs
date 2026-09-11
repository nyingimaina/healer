using Healer.Core.Models;
using Healer.Status.Logic;

namespace Healer.Status.Tests;

public class ActionHistoryFormatterTests
{
    [Fact]
    public void FormatRow_IncludesTimestampTypeTargetOutcomeAndReason()
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
        Assert.Contains("unhealthy for 90s", row);
        Assert.DoesNotContain("DRY-RUN", row);
    }

    [Fact]
    public void FormatRow_TruncatesAnOverlyLongReasonRatherThanBreakingColumnAlignment()
    {
        var longReason = new string('x', 100);
        var record = new ActionHistoryRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ActionType = ActionType.CriticalThresholdRestart,
            Target = "worker",
            TriggerReason = longReason,
            DryRun = false,
            Outcome = ActionOutcomeStatus.Success,
        };

        var row = ActionHistoryFormatter.FormatRow(record);

        Assert.DoesNotContain(longReason, row);
        Assert.Contains("…", row);
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

    [Theory]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("this is definitely too long", 10, "this is d…")]
    [InlineData("", 10, "")]
    public void TruncateForDisplay_TruncatesOnlyWhenLongerThanMaxLength(string text, int maxLength, string expected)
    {
        Assert.Equal(expected, ActionHistoryFormatter.TruncateForDisplay(text, maxLength));
    }

    [Fact]
    public void FormatReasonDetail_ReturnsTheFullUntruncatedReason()
    {
        var longReason = new string('x', 200);
        var record = new ActionHistoryRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ActionType = ActionType.CrashLoopRestart,
            Target = "billing-api",
            TriggerReason = longReason,
            DryRun = false,
            Outcome = ActionOutcomeStatus.Success,
        };

        var detail = ActionHistoryFormatter.FormatReasonDetail(record);

        Assert.Contains(longReason, detail);
    }

    [Fact]
    public void FormatReasonDetail_AppendsDetailWhenPresent()
    {
        var record = new ActionHistoryRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ActionType = ActionType.CrashLoopRestart,
            Target = "billing-api",
            TriggerReason = "unhealthy for 90s",
            DryRun = false,
            Outcome = ActionOutcomeStatus.Failed,
            Detail = "still unhealthy after restart",
        };

        var detail = ActionHistoryFormatter.FormatReasonDetail(record);

        Assert.Contains("unhealthy for 90s", detail);
        Assert.Contains("still unhealthy after restart", detail);
    }

    [Fact]
    public void FormatReasonDetail_OmitsDetailLineWhenAbsent()
    {
        var record = new ActionHistoryRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            ActionType = ActionType.CrashLoopRestart,
            Target = "billing-api",
            TriggerReason = "unhealthy for 90s",
            DryRun = false,
            Outcome = ActionOutcomeStatus.Success,
        };

        var detail = ActionHistoryFormatter.FormatReasonDetail(record);

        Assert.Equal("unhealthy for 90s", detail);
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
