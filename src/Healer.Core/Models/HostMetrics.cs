namespace Healer.Core.Models;

/// <summary>Point-in-time host resource usage, read directly from /proc by an <see cref="Abstractions.IHostMetricsProvider"/>.</summary>
public sealed record HostMetrics
{
    public required double MemUsedPercent { get; init; }

    /// <summary>Total physical RAM in bytes — needed to evaluate container memory against a host-relative fallback when a container has no mem_limit.</summary>
    public required long TotalMemoryBytes { get; init; }

    public required double SwapUsedPercent { get; init; }

    public required double LoadAvg1 { get; init; }

    public required double LoadAvg5 { get; init; }

    public required double LoadAvg15 { get; init; }

    public required int CpuCoreCount { get; init; }

    /// <summary>Disk used percent keyed by mount path (e.g. "/", "/var/lib/docker").</summary>
    public required IReadOnlyDictionary<string, double> DiskUsedPercentByMount { get; init; }

    /// <summary>When this host last booted, read from /proc/uptime. The only reliable way to tell
    /// whether a genuine host reboot happened, as opposed to just the `healer` service restarting —
    /// see <see cref="Decision.RebootVerifier"/>.</summary>
    public required DateTimeOffset BootTimeUtc { get; init; }
}
