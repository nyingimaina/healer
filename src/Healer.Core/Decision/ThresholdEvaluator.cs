using Healer.Core.Configuration;
using Healer.Core.Models;

namespace Healer.Core.Decision;

/// <summary>
/// Pure evaluation of host + container metrics against configured thresholds, producing
/// a flat list of incidents. No I/O, no side effects — everything it needs is passed in.
/// </summary>
public static class ThresholdEvaluator
{
    public static IReadOnlyList<Incident> Evaluate(
        HostMetrics host,
        IReadOnlyList<ContainerInfo> containers,
        ThresholdsConfig thresholds,
        IReadOnlyList<ContainerOverrideConfig> overrides,
        IReadOnlyDictionary<string, int> previousRestartCounts)
    {
        var incidents = new List<Incident>();

        EvaluateHost(host, thresholds, incidents);

        // How many RUNNING containers are competing for host memory with no mem_limit of their own -
        // drives AdaptiveMemoryThreshold's fair-share fallback below. Docker is queried with all=true
        // (see DockerSocketHttpClient), so `containers` includes stopped/exited ones too - a stopped
        // container uses no memory and must NOT dilute the fair share of one that's actually running.
        var unlimitedContainerCount = containers.Count(c => c.IsRunning && (c.MemLimitBytes is not { } limit || limit <= 0));

        foreach (var container in containers)
        {
            EvaluateContainerCrashLoop(container, thresholds, previousRestartCounts, incidents);
            EvaluateContainerMemory(container, thresholds, overrides, host.TotalMemoryBytes, unlimitedContainerCount, incidents);
        }

        return incidents;
    }

    private static void EvaluateHost(HostMetrics host, ThresholdsConfig t, List<Incident> incidents)
    {
        AddIfBreached(incidents, ActionType.HostPressureRelief, "host", host.MemUsedPercent, t.HostMemoryWarningPercent, t.HostMemoryCriticalPercent, pct => $"host memory at {pct:F0}%");

        if (host.SwapUsedPercent >= t.HostSwapCriticalPercent)
        {
            incidents.Add(Critical(ActionType.HostPressureRelief, "host", $"host swap at {host.SwapUsedPercent:F0}%"));
        }

        foreach (var mount in t.DiskMountsToCheck)
        {
            if (host.DiskUsedPercentByMount.TryGetValue(mount, out var usedPct))
            {
                AddIfBreached(incidents, ActionType.HostPressureRelief, "host", usedPct, t.DiskWarningPercent, t.DiskCriticalPercent, pct => $"disk {mount} at {pct:F0}%");
            }
        }

        var loadCriticalThreshold = t.LoadAvg1CriticalMultiplierOfCores * Math.Max(1, host.CpuCoreCount);
        if (host.LoadAvg1 >= loadCriticalThreshold)
        {
            incidents.Add(Critical(ActionType.HostPressureRelief, "host", $"1-minute load average {host.LoadAvg1:F1} exceeds {loadCriticalThreshold:F1} ({host.CpuCoreCount} cores)"));
        }
    }

    private static void EvaluateContainerCrashLoop(
        ContainerInfo container,
        ThresholdsConfig t,
        IReadOnlyDictionary<string, int> previousRestartCounts,
        List<Incident> incidents)
    {
        if (container.HealthStatus == ContainerHealthStatus.Unhealthy
            && container.UnhealthyFor is { } unhealthyFor
            && unhealthyFor >= TimeSpan.FromSeconds(t.UnhealthyGraceSeconds))
        {
            incidents.Add(Critical(ActionType.CrashLoopRestart, container.Name, $"unhealthy for {unhealthyFor.TotalSeconds:F0}s"));
            return;
        }

        if (previousRestartCounts.TryGetValue(container.Name, out var previousCount) && container.RestartCount > previousCount)
        {
            incidents.Add(Critical(ActionType.CrashLoopRestart, container.Name, $"restart count jumped from {previousCount} to {container.RestartCount}"));
        }
    }

    private static void EvaluateContainerMemory(
        ContainerInfo container,
        ThresholdsConfig t,
        IReadOnlyList<ContainerOverrideConfig> overrides,
        long hostTotalMemoryBytes,
        int unlimitedContainerCount,
        List<Incident> incidents)
    {
        var overrideConfig = overrides.FirstOrDefault(o => container.Name.Contains(o.NamePattern, StringComparison.OrdinalIgnoreCase));
        var criticalPct = overrideConfig?.ContainerMemoryCriticalPercentOfLimit ?? t.ContainerMemoryCriticalPercentOfLimit;
        var warningPct = t.ContainerMemoryWarningPercentOfLimit;

        double usedPct;
        string basis;
        if (container.MemLimitBytes is { } limit && limit > 0)
        {
            usedPct = 100.0 * container.MemUsedBytes / limit;
            basis = $"of its {FormatBytes(limit)} limit";
        }
        else if (hostTotalMemoryBytes > 0)
        {
            // No mem_limit set: fall back to a fair-share-adjusted host-relative threshold — see
            // AdaptiveMemoryThreshold. A lone unlimited container gets a much higher ceiling than the
            // flat baseline; the baseline is a floor once enough OTHER unlimited containers are
            // competing for the same host memory.
            usedPct = 100.0 * container.MemUsedBytes / hostTotalMemoryBytes;
            var (adaptiveWarningPct, adaptiveCriticalPct) = AdaptiveMemoryThreshold.ComputeNoLimitThresholds(unlimitedContainerCount, t);
            criticalPct = overrideConfig?.ContainerMemoryCriticalPercentOfLimit ?? adaptiveCriticalPct;
            warningPct = adaptiveWarningPct;
            basis = $"of total host memory ({unlimitedContainerCount} unlimited container(s) sharing it)";
        }
        else
        {
            return;
        }

        AddIfBreached(incidents, ActionType.CriticalThresholdRestart, container.Name, usedPct, warningPct, criticalPct,
            pct => $"memory at {pct:F0}% {basis}");
    }

    private static void AddIfBreached(List<Incident> incidents, ActionType action, string target, double value, double warning, double critical, Func<double, string> reason)
    {
        if (value >= critical)
        {
            incidents.Add(Critical(action, target, reason(value)));
        }
        else if (value >= warning)
        {
            incidents.Add(Warning(action, target, reason(value)));
        }
    }

    private static Incident Critical(ActionType action, string target, string reason) =>
        new() { SuggestedAction = action, Target = target, Severity = IncidentSeverity.Critical, Reason = reason };

    private static Incident Warning(ActionType action, string target, string reason) =>
        new() { SuggestedAction = action, Target = target, Severity = IncidentSeverity.Warning, Reason = reason };

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1}GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0}MB",
        _ => $"{bytes}B",
    };
}
