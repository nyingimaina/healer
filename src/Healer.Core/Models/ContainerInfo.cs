namespace Healer.Core.Models;

public enum ContainerHealthStatus
{
    None,
    Starting,
    Healthy,
    Unhealthy,
}

/// <summary>
/// A snapshot of one container's identity, health, and resource usage as observed
/// on a single poll tick. Populated by an <see cref="Abstractions.IContainerRuntime"/>.
/// </summary>
public sealed record ContainerInfo
{
    /// <summary>Stable, human-meaningful name (NOT the Docker container id, which changes on recreation).</summary>
    public required string Name { get; init; }

    public required string Id { get; init; }

    public required bool IsRunning { get; init; }

    public required ContainerHealthStatus HealthStatus { get; init; }

    /// <summary>How long the container has been continuously unhealthy, or null if not currently unhealthy.</summary>
    public TimeSpan? UnhealthyFor { get; init; }

    public required int RestartCount { get; init; }

    public required long MemUsedBytes { get; init; }

    /// <summary>The container's own mem_limit in bytes, or null when no limit is configured (HostConfig.Memory == 0).</summary>
    public long? MemLimitBytes { get; init; }

    public required double CpuPercent { get; init; }
}
