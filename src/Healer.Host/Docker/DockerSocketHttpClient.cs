using Healer.Core.Abstractions;
using Healer.Core.Models;

namespace Healer.Host.Docker;

/// <summary>
/// IContainerRuntime over the Docker Engine API. Composes list + per-container inspect + per-container
/// stats into Healer.Core's ContainerInfo. Tracks "how long has this container been unhealthy" itself
/// in memory (Docker's API doesn't expose that directly) — this resets across a Healer restart, which
/// is an acceptable, arguably safer, edge case (it just means the unhealthy grace period restarts too).
/// </summary>
public sealed class DockerSocketHttpClient(DockerApiClient api, TimeProvider timeProvider) : IContainerRuntime
{
    private readonly Dictionary<string, DateTimeOffset> _unhealthySinceUtc = [];

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct)
    {
        var listItems = await api.ListContainersAsync(all: true, ct);
        var result = new List<ContainerInfo>(listItems.Count);
        var now = timeProvider.GetUtcNow();
        var stillPresent = new HashSet<string>();

        foreach (var item in listItems)
        {
            var name = item.Names.Count > 0 ? item.Names[0].TrimStart('/') : item.Id;
            var inspect = await api.InspectAsync(name, ct);
            if (inspect is null)
            {
                continue; // container disappeared between list and inspect — skip this tick, it'll show up next tick if still there
            }

            stillPresent.Add(name);

            var healthStatus = MapHealth(inspect.State?.Health?.Status);
            var unhealthyFor = TrackUnhealthyDuration(name, healthStatus, now);

            var (memUsedBytes, cpuPercent) = await TryGetStatsAsync(name, ct);
            var memLimitBytes = inspect.HostConfig?.Memory is { } mem and > 0 ? mem : (long?)null;

            result.Add(new ContainerInfo
            {
                Name = name,
                Id = item.Id,
                IsRunning = inspect.State?.Running ?? false,
                HealthStatus = healthStatus,
                UnhealthyFor = unhealthyFor,
                RestartCount = inspect.RestartCount,
                MemUsedBytes = memUsedBytes,
                MemLimitBytes = memLimitBytes,
                CpuPercent = cpuPercent,
            });
        }

        // Forget unhealthy-tracking for containers that no longer exist, so it doesn't leak forever.
        foreach (var staleName in _unhealthySinceUtc.Keys.Where(n => !stillPresent.Contains(n)).ToList())
        {
            _unhealthySinceUtc.Remove(staleName);
        }

        return result;
    }

    public Task RestartContainerAsync(string containerName, TimeSpan timeout, CancellationToken ct) =>
        api.RestartContainerAsync(containerName, timeout, ct);

    private TimeSpan? TrackUnhealthyDuration(string name, ContainerHealthStatus health, DateTimeOffset now)
    {
        if (health != ContainerHealthStatus.Unhealthy)
        {
            _unhealthySinceUtc.Remove(name);
            return null;
        }

        if (!_unhealthySinceUtc.TryGetValue(name, out var since))
        {
            since = now;
            _unhealthySinceUtc[name] = since;
        }

        return now - since;
    }

    private async Task<(long MemUsedBytes, double CpuPercent)> TryGetStatsAsync(string name, CancellationToken ct)
    {
        var stats = await api.GetStatsAsync(name, ct);
        if (stats?.MemoryStats is null || stats.CpuStats?.CpuUsage is null || stats.PreCpuStats?.CpuUsage is null)
        {
            return (0, 0);
        }

        var memUsed = stats.MemoryStats.Usage;

        var cpuDelta = stats.CpuStats.CpuUsage.TotalUsage - stats.PreCpuStats.CpuUsage.TotalUsage;
        var systemDelta = stats.CpuStats.SystemCpuUsage - stats.PreCpuStats.SystemCpuUsage;
        var onlineCpus = Math.Max(1, stats.CpuStats.OnlineCpus);

        var cpuPercent = systemDelta > 0 && cpuDelta > 0
            ? (double)cpuDelta / systemDelta * onlineCpus * 100.0
            : 0.0;

        return (memUsed, cpuPercent);
    }

    private static ContainerHealthStatus MapHealth(string? dockerStatus) => dockerStatus switch
    {
        "healthy" => ContainerHealthStatus.Healthy,
        "unhealthy" => ContainerHealthStatus.Unhealthy,
        "starting" => ContainerHealthStatus.Starting,
        _ => ContainerHealthStatus.None,
    };
}
