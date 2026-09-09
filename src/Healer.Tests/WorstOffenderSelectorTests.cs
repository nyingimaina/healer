using Healer.Core.Configuration;
using Healer.Core.Decision;
using Healer.Core.Models;

namespace Healer.Tests;

public class WorstOffenderSelectorTests
{
    private static ContainerInfo Make(string name, long memUsed, long? memLimit) => new()
    {
        Name = name,
        Id = name + "-id",
        IsRunning = true,
        HealthStatus = ContainerHealthStatus.Healthy,
        RestartCount = 0,
        MemUsedBytes = memUsed,
        MemLimitBytes = memLimit,
        CpuPercent = 1,
    };

    [Fact]
    public void PicksHighestUsageToLimitRatio_WhenLimitsAreSet()
    {
        var containers = new List<ContainerInfo>
        {
            Make("a", memUsed: 500, memLimit: 1000), // 50%
            Make("b", memUsed: 900, memLimit: 1000), // 90%
        };

        var worst = WorstOffenderSelector.SelectWorstOffender(containers, hostTotalMemoryBytes: 10_000, overrides: []);

        Assert.Equal("b", worst?.Name);
    }

    [Fact]
    public void PicksHighestUsageToHostMemoryRatio_WhenNoLimitIsSet()
    {
        var containers = new List<ContainerInfo>
        {
            Make("a", memUsed: 100, memLimit: null),
            Make("b", memUsed: 400, memLimit: null),
        };

        var worst = WorstOffenderSelector.SelectWorstOffender(containers, hostTotalMemoryBytes: 1000, overrides: []);

        Assert.Equal("b", worst?.Name);
    }

    [Fact]
    public void NormalizesComparisonBetweenLimitedAndUnlimitedContainers()
    {
        var containers = new List<ContainerInfo>
        {
            Make("limited", memUsed: 950, memLimit: 1000),   // 95% of its own limit
            Make("unlimited", memUsed: 100, memLimit: null), // 10% of host memory
        };

        var worst = WorstOffenderSelector.SelectWorstOffender(containers, hostTotalMemoryBytes: 1000, overrides: []);

        Assert.Equal("limited", worst?.Name);
    }

    [Fact]
    public void ExcludedContainer_IsNeverSelected_EvenIfWorst()
    {
        var containers = new List<ContainerInfo>
        {
            Make("db", memUsed: 950, memLimit: 1000),
            Make("app", memUsed: 100, memLimit: 1000),
        };
        var overrides = new List<ContainerOverrideConfig> { new() { NamePattern = "db", ExcludeFromWorstOffenderSelection = true } };

        var worst = WorstOffenderSelector.SelectWorstOffender(containers, hostTotalMemoryBytes: 10_000, overrides);

        Assert.Equal("app", worst?.Name);
    }

    [Fact]
    public void ReturnsNull_WhenNothingIsAboveTheConsiderationThreshold()
    {
        var containers = new List<ContainerInfo>
        {
            Make("a", memUsed: 10, memLimit: 1000), // 1%
            Make("b", memUsed: 20, memLimit: 1000), // 2%
        };

        var worst = WorstOffenderSelector.SelectWorstOffender(containers, hostTotalMemoryBytes: 10_000, overrides: []);

        Assert.Null(worst);
    }

    [Fact]
    public void TiesAreBrokenByAbsoluteUsageBytes()
    {
        var containers = new List<ContainerInfo>
        {
            Make("small", memUsed: 500, memLimit: 1000),   // 50%
            Make("large", memUsed: 5000, memLimit: 10_000), // 50%, but more absolute bytes
        };

        var worst = WorstOffenderSelector.SelectWorstOffender(containers, hostTotalMemoryBytes: 100_000, overrides: []);

        Assert.Equal("large", worst?.Name);
    }
}
