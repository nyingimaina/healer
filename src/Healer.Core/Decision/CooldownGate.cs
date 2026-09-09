using Healer.Core.Configuration;
using Healer.Core.Models;

namespace Healer.Core.Decision;

/// <summary>
/// Enforces "restarts spaced with sensible intervals so availability doesn't degrade further":
/// a single global cooldown across ALL mutating action types (not just restarts — a prune and a
/// restart can't collide either), plus a per-tick concurrency cap. Priority among simultaneous
/// candidates follows <see cref="ActionType"/>'s declared ordinal order.
/// </summary>
public static class CooldownGate
{
    public static IReadOnlyList<PlannedAction> SelectActions(
        IReadOnlyList<PlannedAction> candidates,
        RestartPolicyConfig policy,
        DateTimeOffset? lastGlobalActionUtc,
        DateTimeOffset now)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        if (lastGlobalActionUtc is { } last && now - last < TimeSpan.FromSeconds(policy.GlobalActionCooldownSeconds))
        {
            return [];
        }

        return candidates
            .OrderBy(c => c.Type)
            .Take(Math.Max(1, policy.MaxConcurrentActionsPerTick))
            .ToList();
    }
}
