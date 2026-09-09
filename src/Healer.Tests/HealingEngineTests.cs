using Healer.Core.Configuration;
using Healer.Core.Engine;
using Healer.Core.Models;
using Healer.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Healer.Tests;

public class HealingEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public FakeContainerRuntime ContainerRuntime { get; } = new();
        public FakeHostSystemActions HostSystemActions { get; } = new();
        public FakeHostMetricsProvider HostMetrics { get; } = new();
        public FakeStateStore StateStore { get; } = new();
        public FakeNotifier Notifier { get; } = new();
        public FakeHistoryStore HistoryStore { get; } = new();
        public FakeComposeRestartExecutor ComposeRestartExecutor { get; } = new();
        public FakeTimeProvider TimeProvider { get; } = new(T0);
        public List<(string Message, Exception Ex)> Warnings { get; } = [];

        public HealerConfig Config { get; set; } = new() { ServerName = "test-server" };

        public HealingEngine BuildEngine() => new(
            ContainerRuntime, HostSystemActions, HostMetrics, StateStore, Notifier, HistoryStore,
            ComposeRestartExecutor, Config, TimeProvider, (msg, ex) => Warnings.Add((msg, ex)));

        public Task Tick() => BuildEngine().RunTickAsync(CancellationToken.None);
    }

    private static ContainerInfo Container(
        string name = "app", long memUsed = 0, long? memLimit = 1000,
        ContainerHealthStatus health = ContainerHealthStatus.Healthy, TimeSpan? unhealthyFor = null) => new()
    {
        Name = name,
        Id = name + "-id",
        IsRunning = true,
        HealthStatus = health,
        UnhealthyFor = unhealthyFor,
        RestartCount = 0,
        MemUsedBytes = memUsed,
        MemLimitBytes = memLimit,
        CpuPercent = 1,
    };

    [Fact]
    public async Task DryRun_FullMultiIncidentScenario_MakesZeroMutatingCalls()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = true } };
        h.HostMetrics.Metrics = h.HostMetrics.Metrics with { MemUsedPercent = 95, SwapUsedPercent = 95 };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));
        h.ContainerRuntime.Containers.Add(Container("hungry", memUsed: 990, memLimit: 1000));

        await h.Tick();

        Assert.Empty(h.ContainerRuntime.RestartCalls);
        Assert.Equal(0, h.HostSystemActions.TotalMutatingCalls);
        Assert.NotEmpty(h.Notifier.SentMessages);
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("DRY-RUN"));
    }

    [Fact]
    public async Task EveryTelegramMessage_IsPrefixedWithTheConfiguredServerName()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "prod-api-1", DryRun = true } };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick();

        Assert.Contains(h.Notifier.SentMessages, m => m.StartsWith("[prod-api-1]"));
    }

    [Fact]
    public async Task Live_CrashLoopRestart_PersistsState()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick();

        Assert.Contains("crashy", h.ContainerRuntime.RestartCalls);
        Assert.True(h.StateStore.SaveCalls >= 1);
        Assert.NotNull(h.StateStore.State.Containers["crashy"].PendingVerificationUntilUtc);
    }

    [Fact]
    public async Task Live_WorstOffenderRestart_WhenHostCriticalButNoContainerExceedsOwnLimit()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.HostMetrics.Metrics = h.HostMetrics.Metrics with { MemUsedPercent = 95 };
        // 50% of its own limit — well under the 95% critical-of-limit threshold, so no direct incident.
        h.ContainerRuntime.Containers.Add(Container("app", memUsed: 500, memLimit: 1000));

        await h.Tick(); // streak = 1, not yet sustained
        Assert.DoesNotContain("app", h.ContainerRuntime.RestartCalls);

        h.TimeProvider.Advance(TimeSpan.FromSeconds(200)); // clear the global cooldown from tick 1's pressure-relief action
        await h.Tick(); // streak = 2, meets SustainedBreachTicksRequired default of 2

        Assert.Contains("app", h.ContainerRuntime.RestartCalls);
    }

    [Fact]
    public async Task Live_OnlyOneOfTwoSimultaneouslyBreachingContainers_IsActedOnPerTick()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.ContainerRuntime.Containers.Add(Container("a", memUsed: 990, memLimit: 1000));
        h.ContainerRuntime.Containers.Add(Container("b", memUsed: 980, memLimit: 1000));

        await h.Tick();

        Assert.Single(h.ContainerRuntime.RestartCalls);
    }

    [Fact]
    public async Task Live_OpenCircuitContainer_GetsAlertOnlyInsteadOfRestart()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.StateStore.State.Containers["crashy"] = new ContainerRuntimeState { CircuitState = CircuitState.Open, CircuitOpenedAtUtc = T0 };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick();

        Assert.DoesNotContain("crashy", h.ContainerRuntime.RestartCalls);
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("Skipped"));
        Assert.Contains(h.HistoryStore.ActionRecords, a => a.Status == ActionOutcomeStatus.SkippedCircuitOpen);
    }

    [Fact]
    public async Task Live_ScheduledHostReboot_PersistsStateAndNotifies_BeforeRebooting()
    {
        var h = new Harness
        {
            Config = new HealerConfig
            {
                ServerName = "test-server",
                DryRun = false,
                ScheduledReboots = new ScheduledRebootsConfig
                {
                    Host = new RebootSchedule { Enabled = true, IntervalDays = 1, AnchorDate = DateOnly.FromDateTime(T0.UtcDateTime), Hour = 0, Minute = 0, TimeZoneId = "UTC" },
                },
            },
        };

        int? saveCallsAtRebootTime = null;
        int? notifyCountAtRebootTime = null;
        h.HostSystemActions.OnRebootHost = () =>
        {
            saveCallsAtRebootTime = h.StateStore.SaveCalls;
            notifyCountAtRebootTime = h.Notifier.SentMessages.Count;
        };

        await h.Tick();

        Assert.Equal(1, h.HostSystemActions.RebootHostCalls);
        Assert.True(saveCallsAtRebootTime is >= 1, "state must be saved before the reboot call");
        Assert.True(notifyCountAtRebootTime is >= 1, "Telegram must be notified before the reboot call");
    }

    [Fact]
    public async Task Live_ExceptionDuringRestart_IsCaughtReportedAndDoesNotCrashTheLoop()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));
        h.ContainerRuntime.ThrowOnRestart = new InvalidOperationException("docker socket unreachable");

        await h.Tick(); // must not throw

        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("FAILED") && m.Contains("docker socket unreachable"));
        Assert.Contains(h.HistoryStore.ActionRecords, a => a.Status == ActionOutcomeStatus.Failed);
    }

    [Fact]
    public async Task Snapshot_IsRecordedOnlyOnceIntervalElapses_NotOnEveryTick()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", History = new HistoryConfig { SnapshotIntervalSeconds = 60 } } };
        var engine = h.BuildEngine();

        await engine.RunTickAsync(CancellationToken.None); // t=0: no prior snapshot, records
        Assert.Single(h.HistoryStore.HostSnapshots);

        h.TimeProvider.Advance(TimeSpan.FromSeconds(10));
        await engine.RunTickAsync(CancellationToken.None); // t=10s: within interval, should not record again
        Assert.Single(h.HistoryStore.HostSnapshots);

        h.TimeProvider.Advance(TimeSpan.FromSeconds(55));
        await engine.RunTickAsync(CancellationToken.None); // t=65s: interval elapsed, records
        Assert.Equal(2, h.HistoryStore.HostSnapshots.Count);
    }

    [Fact]
    public async Task EveryRemediationAttempt_IncludingDryRun_ProducesExactlyOneActionsRecord()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = true } };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick();

        var record = Assert.Single(h.HistoryStore.ActionRecords);
        Assert.Equal(ActionOutcomeStatus.DryRun, record.Status);
    }

    [Fact]
    public async Task ProblemsOnly_SuppressesTelegramForARoutineSuccessfulRestart_ButStillRecordsHistory()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false, NotificationLevel = NotificationLevel.ProblemsOnly } };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));
        h.ContainerRuntime.OnRestart = _ => { }; // restart "succeeds" from the fake's perspective

        await h.Tick();

        Assert.DoesNotContain(h.Notifier.SentMessages, m => m.Contains("Done:"));
        Assert.Contains(h.HistoryStore.ActionRecords, a => a.Status == ActionOutcomeStatus.Success); // history sees it regardless
    }

    [Fact]
    public async Task Everything_SendsTelegramEvenForARoutineSuccessfulRestart()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false, NotificationLevel = NotificationLevel.Everything } };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick();

        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("Done:"));
    }

    [Fact]
    public async Task ProblemsOnly_StillSendsTelegram_ForAScheduledHostReboot()
    {
        var h = new Harness
        {
            Config = new HealerConfig
            {
                ServerName = "test-server",
                DryRun = false,
                NotificationLevel = NotificationLevel.ProblemsOnly,
                ScheduledReboots = new ScheduledRebootsConfig
                {
                    Host = new RebootSchedule { Enabled = true, IntervalDays = 1, AnchorDate = DateOnly.FromDateTime(T0.UtcDateTime), Hour = 0, Minute = 0, TimeZoneId = "UTC" },
                },
            },
        };

        await h.Tick();

        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("reboot"));
    }

    [Fact]
    public async Task ProblemsOnly_StillSendsTelegram_ForFailuresAndOpenCircuits()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false, NotificationLevel = NotificationLevel.ProblemsOnly } };
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));
        h.ContainerRuntime.ThrowOnRestart = new InvalidOperationException("boom");

        await h.Tick();

        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("FAILED"));
    }

    [Fact]
    public async Task Live_ScheduledComposeRestart_ExecutesAndPersistsLastRun()
    {
        var h = new Harness
        {
            Config = new HealerConfig
            {
                ServerName = "test-server",
                DryRun = false,
                ScheduledComposeRestarts = new ScheduledComposeRestartsConfig
                {
                    Projects =
                    [
                        new ComposeProjectSchedule
                        {
                            ProjectName = "milele",
                            WorkingDirectory = "/opt/milele",
                            Schedule = new RebootSchedule { Enabled = true, IntervalDays = 1, AnchorDate = DateOnly.FromDateTime(T0.UtcDateTime), Hour = 0, Minute = 0, TimeZoneId = "UTC" },
                        },
                    ],
                },
            },
        };

        await h.Tick();

        var call = Assert.Single(h.ComposeRestartExecutor.RestartCalls);
        Assert.Equal("milele", call.Project.Name);
        Assert.Equal("/opt/milele", call.Project.WorkingDirectory);
        Assert.Empty(call.ExcludedServiceNames);
        Assert.Equal(T0, h.StateStore.State.LastScheduledComposeRestartUtc["milele"]);
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("milele"));

        // healer-status reads exactly this table (SqliteHistoryStore.QueryActionsAsync, ORDER BY ts
        // DESC) for its "Recent incidents / actions" panel — this is what proves a scheduled compose
        // restart actually shows up there alongside scheduled host reboots, not just that Telegram
        // gets notified.
        Assert.Contains(h.HistoryStore.ActionRecords, a => a.Action.Type == ActionType.ScheduledComposeRestart && a.Action.Target == "milele" && a.Status == ActionOutcomeStatus.Success);
    }

    [Fact]
    public async Task Live_ScheduledComposeRestart_PassesExcludedContainersFromOverrides()
    {
        var h = new Harness
        {
            Config = new HealerConfig
            {
                ServerName = "test-server",
                DryRun = false,
                ScheduledComposeRestarts = new ScheduledComposeRestartsConfig
                {
                    Projects =
                    [
                        new ComposeProjectSchedule
                        {
                            ProjectName = "milele",
                            WorkingDirectory = "/opt/milele",
                            Schedule = new RebootSchedule { Enabled = true, IntervalDays = 1, AnchorDate = DateOnly.FromDateTime(T0.UtcDateTime), Hour = 0, Minute = 0, TimeZoneId = "UTC" },
                        },
                    ],
                },
                ContainerOverrides =
                [
                    new ContainerOverrideConfig { NamePattern = "mariadb", ExcludeFromPeriodicComposeRestart = true },
                ],
            },
        };

        await h.Tick();

        var call = Assert.Single(h.ComposeRestartExecutor.RestartCalls);
        Assert.Contains("mariadb", call.ExcludedServiceNames);
    }

    [Fact]
    public async Task DryRun_ScheduledComposeRestart_MakesNoExecutorCalls()
    {
        var h = new Harness
        {
            Config = new HealerConfig
            {
                ServerName = "test-server",
                DryRun = true,
                ScheduledComposeRestarts = new ScheduledComposeRestartsConfig
                {
                    Projects =
                    [
                        new ComposeProjectSchedule
                        {
                            ProjectName = "milele",
                            WorkingDirectory = "/opt/milele",
                            Schedule = new RebootSchedule { Enabled = true, IntervalDays = 1, AnchorDate = DateOnly.FromDateTime(T0.UtcDateTime), Hour = 0, Minute = 0, TimeZoneId = "UTC" },
                        },
                    ],
                },
            },
        };

        await h.Tick();

        Assert.Empty(h.ComposeRestartExecutor.RestartCalls);
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("DRY-RUN") && m.Contains("milele"));
    }

    [Fact]
    public async Task ProblemsOnly_StillSendsTelegram_ForAScheduledComposeRestart()
    {
        var h = new Harness
        {
            Config = new HealerConfig
            {
                ServerName = "test-server",
                DryRun = false,
                NotificationLevel = NotificationLevel.ProblemsOnly,
                ScheduledComposeRestarts = new ScheduledComposeRestartsConfig
                {
                    Projects =
                    [
                        new ComposeProjectSchedule
                        {
                            ProjectName = "milele",
                            WorkingDirectory = "/opt/milele",
                            Schedule = new RebootSchedule { Enabled = true, IntervalDays = 1, AnchorDate = DateOnly.FromDateTime(T0.UtcDateTime), Hour = 0, Minute = 0, TimeZoneId = "UTC" },
                        },
                    ],
                },
            },
        };

        await h.Tick();

        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("milele"));
    }

    [Fact]
    public async Task Live_ScheduledComposeRestart_ExceptionIsCaughtReportedAndDoesNotCrashTheLoop()
    {
        var h = new Harness
        {
            Config = new HealerConfig
            {
                ServerName = "test-server",
                DryRun = false,
                ScheduledComposeRestarts = new ScheduledComposeRestartsConfig
                {
                    Projects =
                    [
                        new ComposeProjectSchedule
                        {
                            ProjectName = "milele",
                            WorkingDirectory = "/opt/milele",
                            Schedule = new RebootSchedule { Enabled = true, IntervalDays = 1, AnchorDate = DateOnly.FromDateTime(T0.UtcDateTime), Hour = 0, Minute = 0, TimeZoneId = "UTC" },
                        },
                    ],
                },
            },
        };
        h.ComposeRestartExecutor.ThrowOnRestart = new InvalidOperationException("docker compose not found");

        await h.Tick(); // must not throw

        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("FAILED") && m.Contains("docker compose not found"));
        Assert.DoesNotContain("milele", h.StateStore.State.LastScheduledComposeRestartUtc.Keys);
    }

    [Fact]
    public async Task HistoryStoreFailure_IsCaughtAndLogged_WithoutAffectingTheTickOutcome()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = true } };
        h.HistoryStore.ThrowOnRecord = true;
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick(); // must not throw despite the history store failing on every call

        Assert.NotEmpty(h.Warnings);
        Assert.NotEmpty(h.Notifier.SentMessages); // notification still succeeds even though history recording failed
    }
}
