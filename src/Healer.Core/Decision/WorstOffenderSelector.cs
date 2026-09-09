using Healer.Core.Configuration;
using Healer.Core.Models;

namespace Healer.Core.Decision;

/// <summary>
/// Picks the single container most likely to trigger the kernel OOM-killer next, so it can be
/// restarted pre-emptively instead of letting the kernel pick an arbitrary victim. Normalizes
/// containers with a mem_limit and containers without one onto the same 0..1 ratio scale.
/// </summary>
public static class WorstOffenderSelector
{
    /// <summary>Minimum usage ratio (of its limit, or of host memory when unlimited) before a container is even considered — avoids picking an essentially-idle container just because it's the "least small" one when the real cause of host pressure lies elsewhere (e.g. disk).</summary>
    public const double DefaultMinRatioToConsider = 0.05;

    public static ContainerInfo? SelectWorstOffender(
        IReadOnlyList<ContainerInfo> containers,
        long hostTotalMemoryBytes,
        IReadOnlyList<ContainerOverrideConfig> overrides,
        double minRatioToConsider = DefaultMinRatioToConsider)
    {
        ContainerInfo? best = null;
        var bestRatio = -1.0;

        foreach (var container in containers)
        {
            if (IsExcluded(container.Name, overrides))
            {
                continue;
            }

            var ratio = ComputeRatio(container, hostTotalMemoryBytes);

            var isNewBest = ratio > bestRatio
                || (ratio == bestRatio && best is not null && container.MemUsedBytes > best.MemUsedBytes);

            if (isNewBest)
            {
                bestRatio = ratio;
                best = container;
            }
        }

        return bestRatio >= minRatioToConsider ? best : null;
    }

    private static double ComputeRatio(ContainerInfo container, long hostTotalMemoryBytes)
    {
        if (container.MemLimitBytes is { } limit && limit > 0)
        {
            return (double)container.MemUsedBytes / limit;
        }

        return hostTotalMemoryBytes > 0 ? (double)container.MemUsedBytes / hostTotalMemoryBytes : 0.0;
    }

    private static bool IsExcluded(string name, IReadOnlyList<ContainerOverrideConfig> overrides) =>
        overrides.Any(o => o.ExcludeFromWorstOffenderSelection && name.Contains(o.NamePattern, StringComparison.OrdinalIgnoreCase));
}
