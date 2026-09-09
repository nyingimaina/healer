using Healer.Core.Abstractions;
using Healer.Core.Models;

namespace Healer.Tests.Fakes;

public sealed class FakeHostMetricsProvider : IHostMetricsProvider
{
    public HostMetrics Metrics { get; set; } = new()
    {
        MemUsedPercent = 10,
        TotalMemoryBytes = 1L << 30,
        SwapUsedPercent = 0,
        LoadAvg1 = 0.1,
        LoadAvg5 = 0.1,
        LoadAvg15 = 0.1,
        CpuCoreCount = 2,
        DiskUsedPercentByMount = new Dictionary<string, double> { ["/"] = 10 },
    };

    public Task<HostMetrics> GetHostMetricsAsync(CancellationToken ct) => Task.FromResult(Metrics);
}
