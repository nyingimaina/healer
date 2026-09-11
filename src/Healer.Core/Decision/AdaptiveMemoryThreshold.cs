using Healer.Core.Configuration;

namespace Healer.Core.Decision;

/// <summary>
/// Computes the effective no-mem_limit critical/warning thresholds for a container, adjusted for how
/// many OTHER containers on the box also have no mem_limit set and are therefore competing for the
/// same unallocated host memory. A single unlimited container on an otherwise-idle box gets a much
/// higher ceiling than the flat baseline would allow (its fair share is effectively the whole host);
/// as more unlimited containers compete for that memory, the fair share shrinks toward - and below -
/// the configured baseline, which acts as a floor. This never makes the threshold STRICTER than the
/// configured baseline, only more lenient when there's a genuine fair-share justification for it.
/// </summary>
public static class AdaptiveMemoryThreshold
{
    public static (double WarningPercent, double CriticalPercent) ComputeNoLimitThresholds(
        int unlimitedContainerCount, ThresholdsConfig t)
    {
        var count = Math.Max(1, unlimitedContainerCount);
        var fairShare = 100.0 / count;

        var criticalPct = Math.Max(t.ContainerMemoryCriticalPercentOfHostWhenNoLimit, fairShare * t.ContainerMemoryCriticalFairShareSafetyFactor);
        var warningPct = Math.Max(t.ContainerMemoryWarningPercentOfHostWhenNoLimit, fairShare * t.ContainerMemoryWarningFairShareSafetyFactor);

        // A genuine leak still needs to trip before it OOM-kills the box, however few containers are
        // sharing it - and warning must stay meaningfully ahead of critical, not collapse into it.
        criticalPct = Math.Min(criticalPct, 90);
        warningPct = Math.Min(warningPct, criticalPct - 10);

        return (warningPct, criticalPct);
    }
}
