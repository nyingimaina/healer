namespace Healer.Core.History;

/// <summary>The two-tier retention policy: raw resource snapshots are pruned much sooner than action/incident history.</summary>
public sealed record RetentionPolicy
{
    public required int ResourceSnapshotRetentionDays { get; init; }

    public required int ActionHistoryRetentionDays { get; init; }

    public static RetentionPolicy FromConfig(Configuration.HistoryConfig config) => new()
    {
        ResourceSnapshotRetentionDays = config.ResourceSnapshotRetentionDays,
        ActionHistoryRetentionDays = config.ActionHistoryRetentionDays,
    };
}

/// <summary>The two cutoff instants a prune pass should delete strictly-older-than. Pure function of (policy, now).</summary>
public static class RetentionCutoffCalculator
{
    public static DateTimeOffset SnapshotCutoffUtc(RetentionPolicy policy, DateTimeOffset nowUtc) =>
        nowUtc.AddDays(-policy.ResourceSnapshotRetentionDays);

    public static DateTimeOffset ActionCutoffUtc(RetentionPolicy policy, DateTimeOffset nowUtc) =>
        nowUtc.AddDays(-policy.ActionHistoryRetentionDays);
}
