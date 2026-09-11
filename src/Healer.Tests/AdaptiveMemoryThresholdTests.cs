using Healer.Core.Configuration;
using Healer.Core.Decision;

namespace Healer.Tests;

public class AdaptiveMemoryThresholdTests
{
    private static readonly ThresholdsConfig Thresholds = new();

    [Fact]
    public void SingleUnlimitedContainer_GetsTheFullFairShareCeiling()
    {
        // Nothing else on the box is competing for memory, so its fair share is effectively the
        // whole host: 100% * the 0.8 critical safety factor = 80%, 100% * 0.6 = 60% warning.
        var (warning, critical) = AdaptiveMemoryThreshold.ComputeNoLimitThresholds(1, Thresholds);

        Assert.Equal(80, critical);
        Assert.Equal(60, warning);
    }

    [Fact]
    public void TwoUnlimitedContainers_HalvesTheFairShare()
    {
        var (warning, critical) = AdaptiveMemoryThreshold.ComputeNoLimitThresholds(2, Thresholds);

        Assert.Equal(40, critical);
        Assert.Equal(30, warning);
    }

    [Fact]
    public void ManyUnlimitedContainers_FallsBackToTheConfiguredFlatBaseline()
    {
        // Once fair share drops below the configured baseline, the baseline acts as a floor - this
        // never makes the threshold STRICTER than today's flat behavior, only more lenient.
        var (warning, critical) = AdaptiveMemoryThreshold.ComputeNoLimitThresholds(10, Thresholds);

        Assert.Equal(Thresholds.ContainerMemoryCriticalPercentOfHostWhenNoLimit, critical);
        Assert.Equal(Thresholds.ContainerMemoryWarningPercentOfHostWhenNoLimit, warning);
    }

    [Fact]
    public void CriticalNeverExceedsA90PercentCeiling_EvenWithAnAggressiveSafetyFactor()
    {
        var aggressive = Thresholds with { ContainerMemoryCriticalFairShareSafetyFactor = 1.0 };

        var (_, critical) = AdaptiveMemoryThreshold.ComputeNoLimitThresholds(1, aggressive);

        Assert.Equal(90, critical);
    }

    [Fact]
    public void WarningAlwaysStaysAtLeastTenPointsBelowCritical()
    {
        var narrowGap = Thresholds with { ContainerMemoryWarningFairShareSafetyFactor = 0.95 };

        var (warning, critical) = AdaptiveMemoryThreshold.ComputeNoLimitThresholds(1, narrowGap);

        Assert.True(warning <= critical - 10);
    }

    [Fact]
    public void ZeroOrNegativeCount_IsTreatedAsOneContainer()
    {
        var (_, criticalForZero) = AdaptiveMemoryThreshold.ComputeNoLimitThresholds(0, Thresholds);
        var (_, criticalForOne) = AdaptiveMemoryThreshold.ComputeNoLimitThresholds(1, Thresholds);

        Assert.Equal(criticalForOne, criticalForZero);
    }
}
