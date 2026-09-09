using System.Text.Json;
using Healer.Core.Configuration;
using Healer.Host.Serialization;

namespace Healer.Host.Tests;

/// <summary>
/// Regression coverage for a real bug found during development: System.Text.Json's SOURCE-GENERATED
/// deserializer (required for Native AOT — see HealerJsonContext) silently discarded C# property
/// initializer defaults for any field missing from the JSON, for ordinary `{ get; init; } = value`
/// properties — a nested config section would deserialize to null, and a scalar like a threshold
/// percentage would silently become 0 instead of its documented default. Fixed by converting every
/// Healer.Core.Configuration record to a positional record with constructor-parameter defaults
/// (verified empirically to behave correctly). These tests exercise the REAL HealerJsonContext, not
/// just C# object-initializer syntax, since that's exactly the code path that had the bug.
/// </summary>
public class HealerJsonRoundTripTests
{
    [Fact]
    public void MinimalConfig_WithOnlyServerName_FillsInEveryOtherDefaultCorrectly()
    {
        var config = Deserialize("""{"serverName":"prod-api-1"}""");

        Assert.Equal("prod-api-1", config.ServerName);
        Assert.True(config.DryRun);
        Assert.Equal(15, config.PollIntervalSeconds);
        Assert.Equal(NotificationLevel.ProblemsOnly, config.NotificationLevel);

        Assert.NotNull(config.Thresholds);
        Assert.Equal(90, config.Thresholds.HostMemoryCriticalPercent); // NOT 0 — this is the exact bug this test guards against
        Assert.Equal(2, config.Thresholds.DiskMountsToCheck.Count);

        Assert.NotNull(config.RestartPolicy);
        Assert.Equal(5, config.RestartPolicy.CircuitBreakerFailureThreshold);
        Assert.Equal(5, config.RestartPolicy.BackoffStagesSeconds.Count);

        Assert.NotNull(config.HostPressureRelief);
        Assert.True(config.HostPressureRelief.EnablePruneStoppedContainers);

        Assert.NotNull(config.ScheduledReboots);
        Assert.NotNull(config.ScheduledReboots.Host);
        Assert.False(config.ScheduledReboots.Host.Enabled);
        Assert.Equal(7, config.ScheduledReboots.Host.IntervalDays);
        Assert.Empty(config.ScheduledReboots.Containers);

        Assert.Empty(config.ContainerOverrides);

        Assert.NotNull(config.ScheduledComposeRestarts);
        Assert.Empty(config.ScheduledComposeRestarts.Projects);

        Assert.NotNull(config.History);
        Assert.Equal(15, config.History.ResourceSnapshotRetentionDays);
        Assert.Equal(90, config.History.ActionHistoryRetentionDays);

        Assert.NotNull(config.Logging);
        Assert.Equal("/var/log/healer", config.Logging.LogDirectory);

        Assert.NotNull(config.Telegram);
        Assert.Equal("TELEGRAM_BOT_TOKEN", config.Telegram.BotTokenEnvVar);
    }

    [Fact]
    public void MissingServerName_ThrowsRatherThanSilentlyDefaultingToEmpty()
    {
        Assert.Throws<JsonException>(() => Deserialize("{}"));
    }

    [Fact]
    public void PartiallySpecifiedNestedSection_KeepsUnspecifiedFieldsAtTheirDefaults()
    {
        // "thresholds" is present but only overrides one field — every other Thresholds field must
        // still fall back to its default, not to 0/null.
        var config = Deserialize("""{"serverName":"x","thresholds":{"hostMemoryCriticalPercent":95}}""");

        Assert.Equal(95, config.Thresholds.HostMemoryCriticalPercent); // explicitly overridden
        Assert.Equal(80, config.Thresholds.HostMemoryWarningPercent); // NOT overridden — must still be the default
        Assert.Equal(2, config.Thresholds.SustainedBreachTicksRequired); // NOT overridden — must still be the default
    }

    [Fact]
    public void FullyPopulatedConfig_RoundTripsExactly()
    {
        var original = new HealerConfig
        {
            ServerName = "prod-api-1",
            DryRun = false,
            NotificationLevel = NotificationLevel.Everything,
            Thresholds = new ThresholdsConfig { HostMemoryCriticalPercent = 88 },
            RestartPolicy = new RestartPolicyConfig { CircuitBreakerFailureThreshold = 9 },
        };

        var json = JsonSerializer.Serialize(original, HealerJsonContext.Default.HealerConfig);
        var roundTripped = Deserialize(json);

        Assert.Equal(original.ServerName, roundTripped.ServerName);
        Assert.Equal(original.DryRun, roundTripped.DryRun);
        Assert.Equal(original.NotificationLevel, roundTripped.NotificationLevel);
        Assert.Equal(original.Thresholds.HostMemoryCriticalPercent, roundTripped.Thresholds.HostMemoryCriticalPercent);
        Assert.Equal(original.RestartPolicy.CircuitBreakerFailureThreshold, roundTripped.RestartPolicy.CircuitBreakerFailureThreshold);
    }

    [Fact]
    public void PartiallySpecifiedComposeProjectSchedule_KeepsUnspecifiedScheduleFieldsAtTheirDefaults()
    {
        var config = Deserialize("""
            {"serverName":"x","scheduledComposeRestarts":{"projects":[
                {"projectName":"milele","workingDirectory":"/opt/milele","schedule":{"enabled":true,"intervalDays":7,"anchorDate":"2026-01-01","hour":3}}
            ]}}
            """);

        var project = Assert.Single(config.ScheduledComposeRestarts.Projects);
        Assert.Equal("milele", project.ProjectName);
        Assert.Equal("/opt/milele", project.WorkingDirectory);
        Assert.True(project.Schedule.Enabled);
        Assert.Equal(7, project.Schedule.IntervalDays);
        Assert.Equal(0, project.Schedule.Minute); // NOT overridden — must still be the default
    }

    [Fact]
    public void TheActualShippedSampleConfig_DeserializesWithoutError()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "healer.config.sample.json");
        var json = File.ReadAllText(path);

        var config = Deserialize(json);

        Assert.False(string.IsNullOrWhiteSpace(config.ServerName));
        Assert.NotNull(config.Thresholds);
        Assert.NotNull(config.Logging);
    }

    private static HealerConfig Deserialize(string json) =>
        JsonSerializer.Deserialize(json, HealerJsonContext.Default.HealerConfig)
        ?? throw new InvalidOperationException("Deserialized to null");
}
