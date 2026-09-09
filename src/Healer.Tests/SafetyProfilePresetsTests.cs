using Healer.Core.Configuration;

namespace Healer.Tests;

public class SafetyProfilePresetsTests
{
    [Fact]
    public void Conservative_UsesHigherThresholds_AndDoesNotTouchDryRun()
    {
        var baseConfig = new HealerConfig { ServerName = "test-server", DryRun = false };

        var result = SafetyProfilePresets.ApplyTo(baseConfig, SafetyProfile.Conservative);

        Assert.Equal(95, result.Thresholds.HostMemoryCriticalPercent);
        Assert.Equal(4, result.Thresholds.SustainedBreachTicksRequired);
        Assert.False(result.HostPressureRelief.EnableSwapfile);
        Assert.False(result.DryRun); // profile must never override the separate dry-run choice
    }

    [Fact]
    public void Balanced_MatchesDefaultConfigValues()
    {
        var result = SafetyProfilePresets.ApplyTo(new HealerConfig { ServerName = "test-server" }, SafetyProfile.Balanced);

        Assert.Equal(new ThresholdsConfig().HostMemoryCriticalPercent, result.Thresholds.HostMemoryCriticalPercent);
        Assert.Equal(new RestartPolicyConfig().CircuitBreakerFailureThreshold, result.RestartPolicy.CircuitBreakerFailureThreshold);
    }

    [Fact]
    public void Aggressive_UsesLowerThresholds_AndEnablesMorePressureRelief()
    {
        var result = SafetyProfilePresets.ApplyTo(new HealerConfig { ServerName = "test-server" }, SafetyProfile.Aggressive);

        Assert.Equal(85, result.Thresholds.HostMemoryCriticalPercent);
        Assert.Equal(1, result.Thresholds.SustainedBreachTicksRequired);
        Assert.True(result.HostPressureRelief.EnableSwapfile);
        Assert.True(result.HostPressureRelief.EnablePruneDanglingImages);
    }

    [Theory]
    [InlineData(SafetyProfile.Conservative)]
    [InlineData(SafetyProfile.Balanced)]
    [InlineData(SafetyProfile.Aggressive)]
    public void Describe_ReturnsNonEmptySummary_ForEveryProfile(SafetyProfile profile)
    {
        Assert.False(string.IsNullOrWhiteSpace(SafetyProfilePresets.Describe(profile)));
    }
}
