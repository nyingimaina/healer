using Healer.Core.Models;

namespace Healer.Core.Abstractions;

public interface IHostMetricsProvider
{
    Task<HostMetrics> GetHostMetricsAsync(CancellationToken ct);
}
