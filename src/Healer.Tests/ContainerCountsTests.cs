using Healer.Core.Decision;
using Healer.Core.Models;

namespace Healer.Tests;

public class ContainerCountsTests
{
    private static ContainerInfo Make(string name, bool isRunning = true, long? memLimit = null, ContainerHealthStatus health = ContainerHealthStatus.Healthy) => new()
    {
        Name = name,
        Id = name + "-id",
        IsRunning = isRunning,
        HealthStatus = health,
        RestartCount = 0,
        MemUsedBytes = 0,
        MemLimitBytes = memLimit,
        CpuPercent = 1,
    };

    [Fact]
    public void RunningWithNoMemLimit_CountsOnlyRunningContainersMissingAMemLimit()
    {
        var containers = new[]
        {
            Make("a", isRunning: true, memLimit: null),
            Make("b", isRunning: true, memLimit: 1000),
            Make("c", isRunning: false, memLimit: null), // stopped - must not count
            Make("d", isRunning: true, memLimit: 0),     // zero limit treated the same as "no limit"
        };

        Assert.Equal(2, ContainerCounts.RunningWithNoMemLimit(containers));
    }

    [Fact]
    public void RunningWithNoMemLimit_ReturnsZeroForAnEmptyList()
    {
        Assert.Equal(0, ContainerCounts.RunningWithNoMemLimit([]));
    }

    [Fact]
    public void Unhealthy_CountsOnlyContainersReportingUnhealthy()
    {
        var containers = new[]
        {
            Make("a", health: ContainerHealthStatus.Unhealthy),
            Make("b", health: ContainerHealthStatus.Healthy),
            Make("c", health: ContainerHealthStatus.None),
            Make("d", health: ContainerHealthStatus.Unhealthy),
        };

        Assert.Equal(2, ContainerCounts.Unhealthy(containers));
    }
}
