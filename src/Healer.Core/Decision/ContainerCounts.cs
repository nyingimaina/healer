using Healer.Core.Models;

namespace Healer.Core.Decision;

/// <summary>
/// The one place container-classification predicates live, shared between decision logic
/// (ThresholdEvaluator) and display code (Healer.Status's live summary) — kept here rather than as
/// scattered inline LINQ so both are guaranteed to agree on what "running" and "unlimited" mean.
/// </summary>
public static class ContainerCounts
{
    /// <summary>Running containers with no mem_limit set — the divisor for
    /// AdaptiveMemoryThreshold's fair-share fallback. Docker is queried with all=true (stopped/exited
    /// containers included), so filtering by IsRunning here matters: a stopped container uses no
    /// memory and must not dilute the fair share of one that's actually running.</summary>
    public static int RunningWithNoMemLimit(IReadOnlyList<ContainerInfo> containers) =>
        containers.Count(c => c.IsRunning && (c.MemLimitBytes is not { } limit || limit <= 0));

    public static int Unhealthy(IReadOnlyList<ContainerInfo> containers) =>
        containers.Count(c => c.HealthStatus == ContainerHealthStatus.Unhealthy);
}
