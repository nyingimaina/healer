using Healer.Core.Abstractions;
using Healer.Core.Models;

namespace Healer.Host.Metrics;

/// <summary>
/// Reads host resource usage directly from /proc and the filesystem — no shelling out to `free`/`df`,
/// which avoids locale-dependent CLI output parsing and an extra process spawn every tick.
/// </summary>
public sealed class HostMetricsProvider(IReadOnlyList<string> diskMountsToCheck) : IHostMetricsProvider
{
    public Task<HostMetrics> GetHostMetricsAsync(CancellationToken ct)
    {
        var (memUsedPercent, totalMemoryBytes, swapUsedPercent) = ReadMemInfo();
        var (load1, load5, load15) = ReadLoadAvg();
        var diskUsage = ReadDiskUsage(diskMountsToCheck);

        return Task.FromResult(new HostMetrics
        {
            MemUsedPercent = memUsedPercent,
            TotalMemoryBytes = totalMemoryBytes,
            SwapUsedPercent = swapUsedPercent,
            LoadAvg1 = load1,
            LoadAvg5 = load5,
            LoadAvg15 = load15,
            CpuCoreCount = Environment.ProcessorCount,
            DiskUsedPercentByMount = diskUsage,
            BootTimeUtc = ReadBootTimeUtc(),
        });
    }

    /// <summary>The only reliable way to tell a genuine host reboot happened, as opposed to just the
    /// `healer` service restarting — see <see cref="Healer.Core.Decision.RebootVerifier"/>.</summary>
    private static DateTimeOffset ReadBootTimeUtc()
    {
        var fields = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var uptimeSeconds = fields.Length > 0 && double.TryParse(fields[0], out var seconds) ? seconds : 0.0;
        return DateTimeOffset.UtcNow - TimeSpan.FromSeconds(uptimeSeconds);
    }

    private static (double MemUsedPercent, long TotalMemoryBytes, double SwapUsedPercent) ReadMemInfo()
    {
        var values = new Dictionary<string, long>();
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            // Format: "MemTotal:       16384000 kB"
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon];
            var rest = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length > 0 && long.TryParse(rest[0], out var kb))
            {
                values[key] = kb;
            }
        }

        var totalKb = values.GetValueOrDefault("MemTotal");
        var availableKb = values.GetValueOrDefault("MemAvailable", values.GetValueOrDefault("MemFree"));
        var swapTotalKb = values.GetValueOrDefault("SwapTotal");
        var swapFreeKb = values.GetValueOrDefault("SwapFree");

        var memUsedPercent = totalKb > 0 ? 100.0 * (totalKb - availableKb) / totalKb : 0.0;
        var swapUsedPercent = swapTotalKb > 0 ? 100.0 * (swapTotalKb - swapFreeKb) / swapTotalKb : 0.0;

        return (memUsedPercent, totalKb * 1024L, swapUsedPercent);
    }

    private static (double Load1, double Load5, double Load15) ReadLoadAvg()
    {
        var fields = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3
            ? (double.Parse(fields[0]), double.Parse(fields[1]), double.Parse(fields[2]))
            : (0.0, 0.0, 0.0);
    }

    private static Dictionary<string, double> ReadDiskUsage(IReadOnlyList<string> mounts)
    {
        var result = new Dictionary<string, double>();

        foreach (var mount in mounts)
        {
            try
            {
                var drive = new DriveInfo(mount);
                if (drive.TotalSize > 0)
                {
                    result[mount] = 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
                }
            }
            catch (Exception)
            {
                // Mount doesn't exist on this box (e.g. a configured mount that isn't present) — skip it
                // rather than fail the whole tick; ThresholdEvaluator simply won't find an entry for it.
            }
        }

        return result;
    }
}
