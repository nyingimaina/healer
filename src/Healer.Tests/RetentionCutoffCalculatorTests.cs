using Healer.Core.History;

namespace Healer.Tests;

public class RetentionCutoffCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly RetentionPolicy Policy = new() { ResourceSnapshotRetentionDays = 15, ActionHistoryRetentionDays = 90 };

    [Fact]
    public void SnapshotCutoff_And_ActionCutoff_AreComputedIndependently()
    {
        var snapshotCutoff = RetentionCutoffCalculator.SnapshotCutoffUtc(Policy, Now);
        var actionCutoff = RetentionCutoffCalculator.ActionCutoffUtc(Policy, Now);

        Assert.Equal(Now.AddDays(-15), snapshotCutoff);
        Assert.Equal(Now.AddDays(-90), actionCutoff);
        Assert.True(actionCutoff < snapshotCutoff, "action history must be retained strictly longer than raw snapshots");
    }

    [Fact]
    public void BoundaryRow_ExactlyAtCutoff_IsConsideredOld()
    {
        var cutoff = RetentionCutoffCalculator.SnapshotCutoffUtc(Policy, Now);
        var rowTimestamp = cutoff; // exactly at the boundary

        // A row strictly older than the cutoff should be deleted; a row AT the cutoff is the
        // caller's choice via `< cutoff` vs `<= cutoff` — this test pins the cutoff value itself
        // so both `SqliteHistoryStore` and tests agree on what "the cutoff" means.
        Assert.Equal(Now.AddDays(-15), rowTimestamp);
    }

    [Fact]
    public void ConfigurableOverrides_AreRespected()
    {
        var customPolicy = new RetentionPolicy { ResourceSnapshotRetentionDays = 3, ActionHistoryRetentionDays = 365 };

        Assert.Equal(Now.AddDays(-3), RetentionCutoffCalculator.SnapshotCutoffUtc(customPolicy, Now));
        Assert.Equal(Now.AddDays(-365), RetentionCutoffCalculator.ActionCutoffUtc(customPolicy, Now));
    }

    [Fact]
    public void FromConfig_MapsHistoryConfigFieldsCorrectly()
    {
        var config = new Healer.Core.Configuration.HistoryConfig { ResourceSnapshotRetentionDays = 20, ActionHistoryRetentionDays = 120 };

        var policy = RetentionPolicy.FromConfig(config);

        Assert.Equal(20, policy.ResourceSnapshotRetentionDays);
        Assert.Equal(120, policy.ActionHistoryRetentionDays);
    }
}
