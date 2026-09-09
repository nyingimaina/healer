namespace Healer.Core.Configuration;

public enum SafetyProfile
{
    Conservative,
    Balanced,
    Aggressive,
}

/// <summary>
/// Maps the three plain-language safety choices offered by the setup wizard to concrete
/// threshold/backoff/pressure-relief values, so the wizard never has to ask about raw
/// numbers. Shared by Healer.Setup (to build the config) and Healer.Tests (to verify it).
/// Never mutates <see cref="HealerConfig.DryRun"/> — that's a separate, explicit wizard choice.
/// </summary>
public static class SafetyProfilePresets
{
    public static HealerConfig ApplyTo(HealerConfig config, SafetyProfile profile) => profile switch
    {
        SafetyProfile.Conservative => config with
        {
            Thresholds = config.Thresholds with
            {
                HostMemoryCriticalPercent = 95,
                ContainerMemoryCriticalPercentOfLimit = 97,
                SustainedBreachTicksRequired = 4,
            },
            RestartPolicy = config.RestartPolicy with
            {
                CircuitBreakerFailureThreshold = 3,
                GlobalActionCooldownSeconds = 180,
            },
            HostPressureRelief = config.HostPressureRelief with
            {
                EnablePruneDanglingImages = false,
                EnableSwapfile = false,
            },
        },

        SafetyProfile.Balanced => config with
        {
            Thresholds = new ThresholdsConfig(),
            RestartPolicy = new RestartPolicyConfig(),
            HostPressureRelief = new HostPressureReliefConfig(),
        },

        SafetyProfile.Aggressive => config with
        {
            Thresholds = config.Thresholds with
            {
                HostMemoryCriticalPercent = 85,
                ContainerMemoryCriticalPercentOfLimit = 90,
                SustainedBreachTicksRequired = 1,
            },
            RestartPolicy = config.RestartPolicy with
            {
                CircuitBreakerFailureThreshold = 8,
                GlobalActionCooldownSeconds = 45,
            },
            HostPressureRelief = config.HostPressureRelief with
            {
                EnablePruneDanglingImages = true,
                EnableSwapfile = true,
            },
        },

        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };

    /// <summary>Plain-language summary shown in the wizard when a profile is highlighted.</summary>
    public static string Describe(SafetyProfile profile) => profile switch
    {
        SafetyProfile.Conservative =>
            "Mostly alerts. Only restarts a container when it's almost certainly failing, waits longer between actions, and never proactively prunes images.",
        SafetyProfile.Balanced =>
            "The recommended middle ground: restarts crashed or clearly overloaded containers promptly, without being trigger-happy.",
        SafetyProfile.Aggressive =>
            "Reacts fastest and cleans up more proactively (image pruning, swapfile creation). Best once you trust Healer's judgement on this box.",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
    };
}
