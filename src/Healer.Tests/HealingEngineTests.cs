using Healer.Core.Configuration;
using Healer.Core.Decision;
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
        public FakeEmergencyStopSignal EmergencyStopSignal { get; } = new();
        public FakeTimeProvider TimeProvider { get; } = new(T0);
        public List<(string Message, Exception Ex)> Warnings { get; } = [];

        public HealerConfig Config { get; set; } = new() { ServerName = "test-server" };

        public HealingEngine BuildEngine() => new(
            ContainerRuntime, HostSystemActions, HostMetrics, StateStore, Notifier, HistoryStore,
            ComposeRestartExecutor, EmergencyStopSignal, Config, TimeProvider, (msg, ex) => Warnings.Add((msg, ex)));

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
        Assert.Equal(T0, h.StateStore.State.PendingRebootRequestedUtc);
        Assert.Equal("scheduled reboot window", h.StateStore.State.PendingRebootReason);
    }

    [Fact]
    public async Task Live_PendingRebootVerified_WhenBootTimeAdvancesPastTheRequest_RecordsSuccessAndClearsTheMarker()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.StateStore.State.PendingRebootRequestedUtc = T0;
        h.StateStore.State.PendingRebootReason = "wizard test";
        h.HostMetrics.Metrics = h.HostMetrics.Metrics with { BootTimeUtc = T0.AddMinutes(2) };
        h.TimeProvider.Advance(TimeSpan.FromMinutes(3));

        await h.Tick();

        Assert.Contains(h.HistoryStore.ActionRecords, a =>
            a.Action.Type == ActionType.HostRebootVerification && a.Status == ActionOutcomeStatus.Success && a.Action.Reason == "wizard test");
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("confirm the host reboot completed"));
        Assert.Null(h.StateStore.State.PendingRebootRequestedUtc);
        Assert.Null(h.StateStore.State.PendingRebootReason);
    }

    [Fact]
    public async Task Live_PendingRebootTimesOut_WhenGraceWindowElapsesWithoutBootTimeAdvancing_RecordsFailureAndClearsTheMarker()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.StateStore.State.PendingRebootRequestedUtc = T0;
        h.StateStore.State.PendingRebootReason = "scheduled reboot window";
        h.HostMetrics.Metrics = h.HostMetrics.Metrics with { BootTimeUtc = T0.AddMinutes(-10) }; // never advanced
        h.TimeProvider.Advance(RebootVerifier.DefaultGraceWindow + TimeSpan.FromMinutes(1));

        await h.Tick();

        Assert.Contains(h.HistoryStore.ActionRecords, a =>
            a.Action.Type == ActionType.HostRebootVerification && a.Status == ActionOutcomeStatus.Failed);
        Assert.Null(h.StateStore.State.PendingRebootRequestedUtc);
    }

    [Fact]
    public async Task Live_PendingRebootStillWaiting_WithinGraceWindow_DoesNothingAndKeepsTheMarker()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.StateStore.State.PendingRebootRequestedUtc = T0;
        h.StateStore.State.PendingRebootReason = "scheduled reboot window";
        h.HostMetrics.Metrics = h.HostMetrics.Metrics with { BootTimeUtc = T0.AddMinutes(-10) };
        h.TimeProvider.Advance(TimeSpan.FromMinutes(5)); // well within the default 30-minute window

        await h.Tick();

        Assert.DoesNotContain(h.HistoryStore.ActionRecords, a => a.Action.Type == ActionType.HostRebootVerification);
        Assert.Equal(T0, h.StateStore.State.PendingRebootRequestedUtc);
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

    private static HealerConfig NightlyComposeRestartConfig(bool backoffEnabled = true) => new()
    {
        ServerName = "test-server",
        DryRun = false,
        NotificationLevel = NotificationLevel.ProblemsOnly,
        ScheduledSuccessBackoff = new ScheduledSuccessBackoffConfig { Enabled = backoffEnabled },
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
    };

    private static int CountComposeRestartNotifications(Harness h) =>
        h.Notifier.SentMessages.Count(m => m.Contains("milele") && m.Contains("compose"));

    [Fact]
    public async Task Live_NightlyComposeRestart_TelegramNotificationsFollowTheDoublingBackoffSchedule_ButHistoryRecordsEveryNight()
    {
        var h = new Harness { Config = NightlyComposeRestartConfig() };

        // Matches the worked example from the design discussion exactly: 6 consecutive successful
        // nights should notify on nights 1, 3, and 6 — 3 notifications, not 6.
        for (var night = 0; night < 6; night++)
        {
            await h.Tick();
            h.TimeProvider.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal(3, CountComposeRestartNotifications(h));
        Assert.Equal(6, h.HistoryStore.ActionRecords.Count(a => a.Action.Type == ActionType.ScheduledComposeRestart && a.Status == ActionOutcomeStatus.Success));
    }

    [Fact]
    public async Task Live_NightlyComposeRestart_ReachesTheCappedSteadyState_NotifyingEvery17thNightThereafter()
    {
        var h = new Harness { Config = NightlyComposeRestartConfig() };

        for (var night = 0; night < 54; night++)
        {
            await h.Tick();
            h.TimeProvider.Advance(TimeSpan.FromDays(1));
        }

        // Per the design's worked example: nights 1, 3, 6, 11, 20, 37, 54 notify — 7 total.
        Assert.Equal(7, CountComposeRestartNotifications(h));
    }

    [Fact]
    public async Task Live_NightlyComposeRestart_AFailureImmediatelyResetsTheBackoff()
    {
        var h = new Harness { Config = NightlyComposeRestartConfig() };

        // Build up a suppressed streak: night 1 notifies, night 2 is suppressed.
        await h.Tick();
        h.TimeProvider.Advance(TimeSpan.FromDays(1));
        await h.Tick();
        h.TimeProvider.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, CountComposeRestartNotifications(h));

        // Night 3 fails outright — failures always notify regardless of backoff state.
        h.ComposeRestartExecutor.ThrowOnRestart = new InvalidOperationException("docker compose down");
        await h.Tick();
        h.TimeProvider.Advance(TimeSpan.FromDays(1));
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("FAILED") && m.Contains("milele"));

        // Night 4 succeeds again — this must notify immediately (streak reset to "day 1"), not
        // still be suppressed as if night 3's failure had never happened.
        h.ComposeRestartExecutor.ThrowOnRestart = null;
        var notificationsBeforeNight4 = CountComposeRestartNotifications(h);
        await h.Tick();

        Assert.True(CountComposeRestartNotifications(h) > notificationsBeforeNight4, "the first success after a failure must notify immediately");
    }

    [Fact]
    public async Task Live_NightlyComposeRestart_BackoffDisabled_NotifiesEveryNightLikeBefore()
    {
        var h = new Harness { Config = NightlyComposeRestartConfig(backoffEnabled: false) };

        for (var night = 0; night < 6; night++)
        {
            await h.Tick();
            h.TimeProvider.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal(6, CountComposeRestartNotifications(h));
    }

    [Fact]
    public async Task Live_NightlyComposeRestart_NotificationLevelEverything_BypassesTheBackoffEntirely()
    {
        var config = NightlyComposeRestartConfig() with { NotificationLevel = NotificationLevel.Everything };
        var h = new Harness { Config = config };

        for (var night = 0; night < 6; night++)
        {
            await h.Tick();
            h.TimeProvider.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal(6, CountComposeRestartNotifications(h));
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

    [Fact]
    public async Task Live_EmergencyBreakerTrips_WhenMutatingActionsExceedTheConfiguredLimit()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false } };
        h.StateStore.State.RecentMutatingActions = Enumerable.Range(0, 15)
            .Select(i => new RecentMutatingAction(T0.AddSeconds(i), ActionType.CrashLoopRestart, "other"))
            .ToList();
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick();

        // The action that pushes the count over the limit still executes — the breaker trips
        // AFTER recording the attempt, it doesn't retroactively block it.
        Assert.Contains("crashy", h.ContainerRuntime.RestartCalls);
        Assert.NotNull(h.EmergencyStopSignal.DisabledReason);
        Assert.Contains(h.HistoryStore.ActionRecords, a => a.Action.Type == ActionType.EmergencyStopTripped);
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("Healer has disabled itself automatically"));
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("15x restart other (crash loop)"));
        Assert.Contains(h.Notifier.SentMessages, m => m.Contains("healer-enable"));
    }

    [Fact]
    public async Task Live_WhenDisabledSentinelIsSet_ActionsAreSkippedSilently_ButRecordedToHistory()
    {
        var h = new Harness { Config = new HealerConfig { ServerName = "test-server", DryRun = false, NotificationLevel = NotificationLevel.ProblemsOnly } };
        h.EmergencyStopSignal.DisabledReason = "manual test";
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick();

        Assert.Empty(h.ContainerRuntime.RestartCalls);
        Assert.Contains(h.HistoryStore.ActionRecords, a => a.Status == ActionOutcomeStatus.Disabled);
        Assert.DoesNotContain(h.Notifier.SentMessages, m => m.Contains("DISABLED"));
    }

    [Fact]
    public async Task Live_EmergencyBreakerDisabledInConfig_NeverTrips_EvenWithManyRecentActions()
    {
        var h = new Harness
        {
            Config = new HealerConfig { ServerName = "test-server", DryRun = false, EmergencyBreaker = new EmergencyBreakerConfig { Enabled = false } },
        };
        h.StateStore.State.RecentMutatingActions = Enumerable.Range(0, 50)
            .Select(i => new RecentMutatingAction(T0.AddSeconds(i), ActionType.CrashLoopRestart, "other"))
            .ToList();
        h.ContainerRuntime.Containers.Add(Container("crashy", health: ContainerHealthStatus.Unhealthy, unhealthyFor: TimeSpan.FromSeconds(120)));

        await h.Tick();

        Assert.Null(h.EmergencyStopSignal.DisabledReason);
        Assert.DoesNotContain(h.HistoryStore.ActionRecords, a => a.Action.Type == ActionType.EmergencyStopTripped);
    }
}
