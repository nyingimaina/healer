using Healer.Core.Configuration;
using Healer.Core.Decision;
using Healer.Core.Models;

namespace Healer.Tests;

public class ThresholdEvaluatorTests
{
    private static readonly ThresholdsConfig Thresholds = new();
    private static readonly IReadOnlyDictionary<string, int> NoPreviousCounts = new Dictionary<string, int>();

    private static HostMetrics MakeHost(
        double memPct = 10, long totalMemBytes = 1_000_000_000, double swapPct = 0,
        double load1 = 0.1, int cores = 4, IReadOnlyDictionary<string, double>? disk = null) => new()
    {
        MemUsedPercent = memPct,
        TotalMemoryBytes = totalMemBytes,
        SwapUsedPercent = swapPct,
        LoadAvg1 = load1,
        LoadAvg5 = load1,
        LoadAvg15 = load1,
        CpuCoreCount = cores,
        DiskUsedPercentByMount = disk ?? new Dictionary<string, double> { ["/"] = 10 },
        BootTimeUtc = DateTimeOffset.UtcNow.AddDays(-1),
    };

    private static ContainerInfo MakeContainer(
        string name = "app", long memUsed = 0, long? memLimit = null,
        ContainerHealthStatus health = ContainerHealthStatus.Healthy, TimeSpan? unhealthyFor = null,
        int restartCount = 0) => new()
    {
        Name = name,
        Id = name + "-id",
        IsRunning = true,
        HealthStatus = health,
        UnhealthyFor = unhealthyFor,
        RestartCount = restartCount,
        MemUsedBytes = memUsed,
        MemLimitBytes = memLimit,
        CpuPercent = 1,
    };

    [Fact]
    public void ContainerBelowWarning_WithMemLimit_ProducesNoIncident()
    {
        var container = MakeContainer(memUsed: 50, memLimit: 1000); // 5%
        var incidents = ThresholdEvaluator.Evaluate(MakeHost(), [container], Thresholds, [], NoPreviousCounts);

        Assert.DoesNotContain(incidents, i => i.Target == "app");
    }

    [Fact]
    public void ContainerBetweenWarningAndCritical_WithMemLimit_ProducesWarning()
    {
        var container = MakeContainer(memUsed: 900, memLimit: 1000); // 90%, between 85 warn / 95 crit
        var incidents = ThresholdEvaluator.Evaluate(MakeHost(), [container], Thresholds, [], NoPreviousCounts);

        var incident = Assert.Single(incidents, i => i.Target == "app");
        Assert.Equal(IncidentSeverity.Warning, incident.Severity);
    }

    [Fact]
    public void ContainerAboveCritical_WithMemLimit_ProducesCritical()
    {
        var container = MakeContainer(memUsed: 960, memLimit: 1000); // 96%
        var incidents = ThresholdEvaluator.Evaluate(MakeHost(), [container], Thresholds, [], NoPreviousCounts);

        var incident = Assert.Single(incidents, i => i.Target == "app");
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
        Assert.Equal(ActionType.CriticalThresholdRestart, incident.SuggestedAction);
    }

    [Fact]
    public void ContainerWithNoMemLimit_FallsBackToHostRelativeThreshold()
    {
        var host = MakeHost(totalMemBytes: 1000);
        var container = MakeContainer(memUsed: 300, memLimit: null); // 30% of host, above the 25% critical fallback

        var incidents = ThresholdEvaluator.Evaluate(host, [container], Thresholds, [], NoPreviousCounts);

        var incident = Assert.Single(incidents, i => i.Target == "app");
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
    }

    [Fact]
    public void ContainerWithNoMemLimit_BelowHostRelativeThreshold_ProducesNoIncident()
    {
        var host = MakeHost(totalMemBytes: 1000);
        var container = MakeContainer(memUsed: 50, memLimit: null); // 5% of host

        var incidents = ThresholdEvaluator.Evaluate(host, [container], Thresholds, [], NoPreviousCounts);

        Assert.DoesNotContain(incidents, i => i.Target == "app");
    }

    [Fact]
    public void PerContainerOverride_TakesPrecedenceOverGlobalThreshold()
    {
        var overrides = new List<ContainerOverrideConfig> { new() { NamePattern = "app", ContainerMemoryCriticalPercentOfLimit = 50 } };
        var container = MakeContainer(memUsed: 600, memLimit: 1000); // 60%, below global 95% but above override's 50%

        var incidents = ThresholdEvaluator.Evaluate(MakeHost(), [container], Thresholds, overrides, NoPreviousCounts);

        var incident = Assert.Single(incidents, i => i.Target == "app");
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
    }

    [Fact]
    public void HostMemoryAboveCritical_ProducesHostIncident()
    {
        var incidents = ThresholdEvaluator.Evaluate(MakeHost(memPct: 95), [], Thresholds, [], NoPreviousCounts);

        var incident = Assert.Single(incidents, i => i.Target == "host");
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
    }

    [Fact]
    public void HostSwapAboveCritical_ProducesHostIncident()
    {
        var incidents = ThresholdEvaluator.Evaluate(MakeHost(swapPct: 95), [], Thresholds, [], NoPreviousCounts);

        Assert.Contains(incidents, i => i.Target == "host" && i.Severity == IncidentSeverity.Critical);
    }

    [Fact]
    public void DiskAboveCritical_OnConfiguredMount_ProducesHostIncident()
    {
        var host = MakeHost(disk: new Dictionary<string, double> { ["/"] = 95, ["/var/lib/docker"] = 10 });
        var incidents = ThresholdEvaluator.Evaluate(host, [], Thresholds, [], NoPreviousCounts);

        Assert.Contains(incidents, i => i.Target == "host" && i.Severity == IncidentSeverity.Critical && i.Reason.Contains('/'));
    }

    [Fact]
    public void LoadAverageAboveCoreMultiplier_ProducesHostIncident()
    {
        var host = MakeHost(load1: 20, cores: 2); // threshold = 4 * 2 = 8
        var incidents = ThresholdEvaluator.Evaluate(host, [], Thresholds, [], NoPreviousCounts);

        Assert.Contains(incidents, i => i.Target == "host" && i.Severity == IncidentSeverity.Critical);
    }

    [Fact]
    public void UnhealthyPastGracePeriod_ProducesCrashLoopIncident()
    {
        var container = MakeContainer(health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120));
        var incidents = ThresholdEvaluator.Evaluate(MakeHost(), [container], Thresholds, [], NoPreviousCounts);

        Assert.Contains(incidents, i => i.Target == "app" && i.SuggestedAction == ActionType.CrashLoopRestart);
    }

    [Fact]
    public void UnhealthyWithinGracePeriod_ProducesNoCrashLoopIncident()
    {
        var container = MakeContainer(health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(5));
        var incidents = ThresholdEvaluator.Evaluate(MakeHost(), [container], Thresholds, [], NoPreviousCounts);

        Assert.DoesNotContain(incidents, i => i.SuggestedAction == ActionType.CrashLoopRestart);
    }

    [Fact]
    public void RestartCountJump_ProducesCrashLoopIncident()
    {
        var container = MakeContainer(restartCount: 4);
        var previous = new Dictionary<string, int> { ["app"] = 2 };

        var incidents = ThresholdEvaluator.Evaluate(MakeHost(), [container], Thresholds, [], previous);

        Assert.Contains(incidents, i => i.Target == "app" && i.SuggestedAction == ActionType.CrashLoopRestart);
    }

    [Fact]
    public void StableRestartCount_ProducesNoCrashLoopIncident()
    {
        var container = MakeContainer(restartCount: 2);
        var previous = new Dictionary<string, int> { ["app"] = 2 };

        var incidents = ThresholdEvaluator.Evaluate(MakeHost(), [container], Thresholds, [], previous);

        Assert.DoesNotContain(incidents, i => i.SuggestedAction == ActionType.CrashLoopRestart);
    }
}
