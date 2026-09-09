using Healer.Core.Abstractions;
using Healer.Core.History;
using Healer.Core.Models;

namespace Healer.Tests.Fakes;

public sealed class FakeHistoryStore : IHealthHistoryStore
{
    public List<(HostMetrics Metrics, DateTimeOffset TimestampUtc)> HostSnapshots { get; } = [];
    public List<(IReadOnlyList<ContainerInfo> Containers, DateTimeOffset TimestampUtc)> ContainerSnapshots { get; } = [];
    public List<ActionOutcome> ActionRecords { get; } = [];
    public List<(RetentionPolicy Policy, DateTimeOffset NowUtc)> PruneCalls { get; } = [];

    /// <summary>When set, every Record* call throws — used to verify a history-store failure never blocks the decision loop.</summary>
    public bool ThrowOnRecord { get; set; }

    public Task RecordHostSnapshotAsync(HostMetrics metrics, DateTimeOffset timestampUtc, CancellationToken ct)
    {
        if (ThrowOnRecord)
        {
            throw new InvalidOperationException("simulated history store failure");
        }

        HostSnapshots.Add((metrics, timestampUtc));
        return Task.CompletedTask;
    }

    public Task RecordContainerSnapshotAsync(IReadOnlyList<ContainerInfo> containers, DateTimeOffset timestampUtc, CancellationToken ct)
    {
        if (ThrowOnRecord)
        {
            throw new InvalidOperationException("simulated history store failure");
        }

        ContainerSnapshots.Add((containers, timestampUtc));
        return Task.CompletedTask;
    }

    public Task RecordActionAsync(ActionOutcome outcome, CancellationToken ct)
    {
        if (ThrowOnRecord)
        {
            throw new InvalidOperationException("simulated history store failure");
        }

        ActionRecords.Add(outcome);
        return Task.CompletedTask;
    }

    public Task PruneAsync(RetentionPolicy policy, DateTimeOffset nowUtc, CancellationToken ct)
    {
        PruneCalls.Add((policy, nowUtc));
        return Task.CompletedTask;
    }

    public Task<PagedResult<ActionHistoryRecord>> QueryActionsAsync(int page, int pageSize, CancellationToken ct) =>
        Task.FromResult(new PagedResult<ActionHistoryRecord>([], false));

    public Task<IReadOnlyList<TrendPoint>> QueryTrendAsync(string metricName, DateTimeOffset sinceUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TrendPoint>>([]);
}
