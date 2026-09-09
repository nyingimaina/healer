using Healer.Core.History;
using Healer.Core.Models;

namespace Healer.Core.Abstractions;

/// <summary>
/// Persists resource snapshots and remediation-action outcomes for later trend/incident review.
/// A diagnostic side-channel only: failures here must never block or crash the decision loop
/// (see HealingEngine, which wraps every call in a try/catch-and-log).
/// </summary>
public interface IHealthHistoryStore
{
    Task RecordHostSnapshotAsync(HostMetrics metrics, DateTimeOffset timestampUtc, CancellationToken ct);

    Task RecordContainerSnapshotAsync(IReadOnlyList<ContainerInfo> containers, DateTimeOffset timestampUtc, CancellationToken ct);

    Task RecordActionAsync(ActionOutcome outcome, CancellationToken ct);

    /// <summary>Deletes snapshot/action rows older than the policy's cutoffs and reclaims space (incremental vacuum).</summary>
    Task PruneAsync(RetentionPolicy policy, DateTimeOffset nowUtc, CancellationToken ct);

    Task<PagedResult<ActionHistoryRecord>> QueryActionsAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>metricName is one of: "host.mem", "host.swap", "host.disk.{mount}", "container.{name}.mem", "container.{name}.cpu".</summary>
    Task<IReadOnlyList<TrendPoint>> QueryTrendAsync(string metricName, DateTimeOffset sinceUtc, CancellationToken ct);
}
