using Healer.Core.Abstractions;
using Healer.Core.Configuration;
using Healer.Core.Decision;
using Healer.Core.History;
using Healer.Core.Models;

namespace Healer.Core.Engine;

/// <summary>
/// Composes every decision component into one poll-tick. Detection (reading host/container state)
/// always happens; every MUTATING call is gated behind `!config.DryRun`. This is the mechanical
/// basis for dry-run being a hard safety guarantee rather than a convention callers must remember.
/// </summary>
public sealed class HealingEngine
{
    private readonly IContainerRuntime _containerRuntime;
    private readonly IHostSystemActions _hostSystemActions;
    private readonly IHostMetricsProvider _hostMetricsProvider;
    private readonly IStateStore _stateStore;
    private readonly INotifier _notifier;
    private readonly IHealthHistoryStore _historyStore;
    private readonly IComposeRestartExecutor _composeRestartExecutor;
    private readonly HealerConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, Exception> _onWarning;

    public HealingEngine(
        IContainerRuntime containerRuntime,
        IHostSystemActions hostSystemActions,
        IHostMetricsProvider hostMetricsProvider,
        IStateStore stateStore,
        INotifier notifier,
        IHealthHistoryStore historyStore,
        IComposeRestartExecutor composeRestartExecutor,
        HealerConfig config,
        TimeProvider? timeProvider = null,
        Action<string, Exception>? onWarning = null)
    {
        _containerRuntime = containerRuntime;
        _hostSystemActions = hostSystemActions;
        _hostMetricsProvider = hostMetricsProvider;
        _stateStore = stateStore;
        _notifier = notifier;
        _historyStore = historyStore;
        _composeRestartExecutor = composeRestartExecutor;
        _config = config;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onWarning = onWarning ?? ((_, _) => { });
    }

    public async Task RunTickAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var state = await _stateStore.LoadAsync(ct);

        var host = await _hostMetricsProvider.GetHostMetricsAsync(ct);
        var containers = await _containerRuntime.ListContainersAsync(ct);

        await VerifyPendingRestartsAsync(state, containers, now, ct);

        var previousRestartCounts = state.Containers
            .Where(kv => kv.Value.LastObservedRestartCount is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.LastObservedRestartCount!.Value);

        var incidents = ThresholdEvaluator.Evaluate(host, containers, _config.Thresholds, _config.ContainerOverrides, previousRestartCounts);

        foreach (var container in containers)
        {
            state.GetOrAddContainer(container.Name).LastObservedRestartCount = container.RestartCount;
        }

        var candidates = BuildCandidates(incidents, containers, host, state, now);

        var eligibleCandidates = await FilterByBackoffAndCircuit(candidates, state, now, ct);
        var chosen = CooldownGate.SelectActions(eligibleCandidates, _config.RestartPolicy, state.LastGlobalActionUtc, now);

        foreach (var action in chosen)
        {
            await ExecuteChosenActionAsync(action, state, now, ct);
        }

        await MaybeRecordSnapshotAsync(host, containers, state, now, ct);
        await MaybeRunRetentionSweepAsync(state, now, ct);

        await _stateStore.SaveAsync(state, ct);
    }

    private List<PlannedAction> BuildCandidates(
        IReadOnlyList<Incident> incidents,
        IReadOnlyList<ContainerInfo> containers,
        HostMetrics host,
        HealerState state,
        DateTimeOffset now)
    {
        var candidates = new List<PlannedAction>();

        // Scheduled host reboot — highest priority, planned rather than reactive.
        if (ScheduleMatcher.IsDue(_config.ScheduledReboots.Host, state.LastScheduledHostRebootUtc, now))
        {
            candidates.Add(new PlannedAction { Type = ActionType.ScheduledHostReboot, Target = "host", Reason = "scheduled reboot window" });
        }

        // Scheduled per-container reboots.
        foreach (var containerSchedule in _config.ScheduledReboots.Containers)
        {
            var lastRun = state.Containers.TryGetValue(containerSchedule.ContainerName, out var cs) ? cs.LastScheduledRebootUtc : null;
            if (ScheduleMatcher.IsDue(containerSchedule.Schedule, lastRun, now))
            {
                candidates.Add(new PlannedAction { Type = ActionType.ScheduledContainerReboot, Target = containerSchedule.ContainerName, Reason = "scheduled restart window" });
            }
        }

        // Scheduled per-project docker-compose restarts.
        foreach (var projectSchedule in _config.ScheduledComposeRestarts.Projects)
        {
            var lastRun = state.LastScheduledComposeRestartUtc.TryGetValue(projectSchedule.ProjectName, out var last) ? last : (DateTimeOffset?)null;
            if (ScheduleMatcher.IsDue(projectSchedule.Schedule, lastRun, now))
            {
                candidates.Add(new PlannedAction { Type = ActionType.ScheduledComposeRestart, Target = projectSchedule.ProjectName, Reason = "scheduled compose refresh window" });
            }
        }

        // Crash-loop / critical-threshold restarts, one candidate per target (highest-priority incident wins if a container has more than one).
        var restartCandidates = incidents
            .Where(i => i.Severity == IncidentSeverity.Critical && i.SuggestedAction != ActionType.HostPressureRelief)
            .GroupBy(i => i.Target)
            .Select(g =>
            {
                var best = g.OrderBy(i => i.SuggestedAction).First();
                return new PlannedAction { Type = best.SuggestedAction, Target = best.Target, Reason = string.Join("; ", g.Select(i => i.Reason)) };
            });
        candidates.AddRange(restartCandidates);

        // Host pressure relief — collapse all critical host-level incidents into one candidate.
        var hostIncidents = incidents.Where(i => i.Severity == IncidentSeverity.Critical && i.SuggestedAction == ActionType.HostPressureRelief).ToList();
        if (hostIncidents.Count > 0)
        {
            candidates.Add(new PlannedAction { Type = ActionType.HostPressureRelief, Target = "host", Reason = string.Join("; ", hostIncidents.Select(i => i.Reason)) });
        }

        // Pre-emptive worst-offender restart — only once host memory has been critical for several consecutive ticks.
        state.HostMemoryCriticalStreak = host.MemUsedPercent >= _config.Thresholds.HostMemoryCriticalPercent
            ? state.HostMemoryCriticalStreak + 1
            : 0;

        if (state.HostMemoryCriticalStreak >= _config.Thresholds.SustainedBreachTicksRequired)
        {
            var worstOffender = WorstOffenderSelector.SelectWorstOffender(containers, host.TotalMemoryBytes, _config.ContainerOverrides);
            if (worstOffender is not null && candidates.All(c => c.Target != worstOffender.Name))
            {
                candidates.Add(new PlannedAction
                {
                    Type = ActionType.PreemptiveWorstOffenderRestart,
                    Target = worstOffender.Name,
                    Reason = "highest memory usage relative to its limit while host memory is critical",
                });
            }
        }

        return candidates;
    }

    private async Task<List<PlannedAction>> FilterByBackoffAndCircuit(List<PlannedAction> candidates, HealerState state, DateTimeOffset now, CancellationToken ct)
    {
        var result = new List<PlannedAction>();

        foreach (var candidate in candidates)
        {
            if (!TargetsAContainer(candidate.Type))
            {
                result.Add(candidate);
                continue;
            }

            var containerState = state.GetOrAddContainer(candidate.Target);
            BackoffCircuitBreakerCalculator.UpdateCircuitTransition(containerState, _config.RestartPolicy, now);

            if (containerState.CircuitState == CircuitState.Open)
            {
                var skipped = new ActionOutcome { Action = candidate, Status = ActionOutcomeStatus.SkippedCircuitOpen, TimestampUtc = now };
                await NotifyAndRecordAsync(skipped, ct);
                continue;
            }

            if (!BackoffCircuitBreakerCalculator.IsEligibleForRestart(containerState, now))
            {
                continue; // still in backoff wait or a prior restart is pending verification — skip silently, don't spam every tick
            }

            result.Add(candidate);
        }

        return result;
    }

    private async Task ExecuteChosenActionAsync(PlannedAction action, HealerState state, DateTimeOffset now, CancellationToken ct)
    {
        if (_config.DryRun)
        {
            var dryRunOutcome = new ActionOutcome { Action = action, Status = ActionOutcomeStatus.DryRun, TimestampUtc = now };
            await NotifyAndRecordAsync(dryRunOutcome, ct);
            return;
        }

        if (action.Type == ActionType.ScheduledHostReboot)
        {
            // Persist state and notify BEFORE rebooting — the process does not survive to report afterward.
            state.LastScheduledHostRebootUtc = now;
            state.LastGlobalActionUtc = now;
            await _stateStore.SaveAsync(state, ct);

            var rebootOutcome = new ActionOutcome { Action = action, Status = ActionOutcomeStatus.Success, TimestampUtc = now };
            await NotifyAndRecordAsync(rebootOutcome, ct);

            await _hostSystemActions.RebootHostAsync(ct);
            return;
        }

        var outcome = await ExecuteLiveActionAsync(action, state, now, ct);
        await NotifyAndRecordAsync(outcome, ct);
    }

    private async Task<ActionOutcome> ExecuteLiveActionAsync(PlannedAction action, HealerState state, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (TargetsAContainer(action.Type))
            {
                await _containerRuntime.RestartContainerAsync(action.Target, TimeSpan.FromSeconds(30), ct);
                var containerState = state.GetOrAddContainer(action.Target);
                BackoffCircuitBreakerCalculator.RecordRestartAttempt(containerState, _config.RestartPolicy, now);
                if (action.Type == ActionType.ScheduledContainerReboot)
                {
                    containerState.LastScheduledRebootUtc = now;
                }
            }
            else if (action.Type == ActionType.HostPressureRelief)
            {
                await ExecuteHostPressureReliefAsync(ct);
            }
            else if (action.Type == ActionType.ScheduledComposeRestart)
            {
                await ExecuteScheduledComposeRestartAsync(action.Target, state, now, ct);
            }

            state.LastGlobalActionUtc = now;
            return new ActionOutcome { Action = action, Status = ActionOutcomeStatus.Success, TimestampUtc = now };
        }
        catch (Exception ex)
        {
            return new ActionOutcome { Action = action, Status = ActionOutcomeStatus.Failed, TimestampUtc = now, Detail = ex.Message };
        }
    }

    private async Task ExecuteScheduledComposeRestartAsync(string projectName, HealerState state, DateTimeOffset now, CancellationToken ct)
    {
        var projectSchedule = _config.ScheduledComposeRestarts.Projects.First(p => p.ProjectName == projectName);
        var excludedServiceNames = _config.ContainerOverrides
            .Where(o => o.ExcludeFromPeriodicComposeRestart)
            .Select(o => o.NamePattern)
            .ToList();

        await _composeRestartExecutor.RestartProjectAsync(
            new ComposeProjectRef(projectSchedule.ProjectName, projectSchedule.WorkingDirectory), excludedServiceNames, ct);

        state.LastScheduledComposeRestartUtc[projectName] = now;
    }

    private async Task ExecuteHostPressureReliefAsync(CancellationToken ct)
    {
        var relief = _config.HostPressureRelief;
        if (relief.EnablePruneStoppedContainers)
        {
            await _hostSystemActions.PruneStoppedContainersAsync(ct);
        }

        if (relief.EnablePruneDanglingImages)
        {
            await _hostSystemActions.PruneDanglingImagesAsync(TimeSpan.FromHours(relief.PruneImageRetentionHours), ct);
        }

        if (relief.EnableSwapfile)
        {
            await _hostSystemActions.EnsureSwapfileAsync(relief.SwapfilePath, relief.SwapfileSizeMb, ct);
        }

        if (relief.EnableDropCaches)
        {
            await _hostSystemActions.DropPageCachesAsync(ct);
        }
    }

    private async Task VerifyPendingRestartsAsync(HealerState state, IReadOnlyList<ContainerInfo> containers, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (name, containerState) in state.Containers.ToList())
        {
            if (containerState.PendingVerificationUntilUtc is not { } pendingUntil || now < pendingUntil)
            {
                continue;
            }

            var current = containers.FirstOrDefault(c => c.Name == name);
            var isHealthy = current is { IsRunning: true } && current.HealthStatus is ContainerHealthStatus.Healthy or ContainerHealthStatus.None;

            if (isHealthy)
            {
                BackoffCircuitBreakerCalculator.RecordVerificationSuccess(containerState);
            }
            else
            {
                BackoffCircuitBreakerCalculator.RecordVerificationFailure(containerState, _config.RestartPolicy, now);
            }

            var verification = new ActionOutcome
            {
                Action = new PlannedAction { Type = ActionType.CrashLoopRestart, Target = name, Reason = "post-restart verification" },
                Status = isHealthy ? ActionOutcomeStatus.Success : ActionOutcomeStatus.Failed,
                TimestampUtc = now,
                Detail = isHealthy ? null : "still unhealthy after restart",
            };
            await NotifyAndRecordAsync(verification, ct);
        }
    }

    private async Task MaybeRecordSnapshotAsync(HostMetrics host, IReadOnlyList<ContainerInfo> containers, HealerState state, DateTimeOffset now, CancellationToken ct)
    {
        if (state.LastHistorySnapshotUtc is { } last && now - last < TimeSpan.FromSeconds(_config.History.SnapshotIntervalSeconds))
        {
            return;
        }

        try
        {
            await _historyStore.RecordHostSnapshotAsync(host, now, ct);
            await _historyStore.RecordContainerSnapshotAsync(containers, now, ct);
            state.LastHistorySnapshotUtc = now;
        }
        catch (Exception ex)
        {
            // History is a diagnostic side-channel — never let it block or crash the decision loop.
            _onWarning("Failed to record health history snapshot", ex);
        }
    }

    private async Task MaybeRunRetentionSweepAsync(HealerState state, DateTimeOffset now, CancellationToken ct)
    {
        var alreadySweptToday = state.LastRetentionSweepUtc is { } last && last.UtcDateTime.Date == now.UtcDateTime.Date;
        if (alreadySweptToday || now.Hour != _config.History.RetentionSweepHour)
        {
            return;
        }

        try
        {
            await _historyStore.PruneAsync(RetentionPolicy.FromConfig(_config.History), now, ct);
            state.LastRetentionSweepUtc = now;
        }
        catch (Exception ex)
        {
            _onWarning("Failed to run health history retention sweep", ex);
        }
    }

    private async Task NotifyAndRecordAsync(ActionOutcome outcome, CancellationToken ct)
    {
        // History always gets everything, unaffected by the Telegram noise preference — it's the
        // durable audit trail (browsable via Healer.Status), separate from what interrupts a phone.
        if (ShouldSendToTelegram(outcome, _config.NotificationLevel))
        {
            var message = MessageFormatter.FormatActionOutcome(outcome);
            await _notifier.SendAsync(MessageFormatter.WithServerPrefix(_config.ServerName, message), ct);
        }

        try
        {
            await _historyStore.RecordActionAsync(outcome, ct);
        }
        catch (Exception ex)
        {
            _onWarning("Failed to record action outcome to health history", ex);
        }
    }

    private static bool ShouldSendToTelegram(ActionOutcome outcome, NotificationLevel level)
    {
        if (level == NotificationLevel.Everything)
        {
            return true;
        }

        // ProblemsOnly: always surface actual problems and disruptive planned events; stay quiet
        // about Healer routinely and successfully doing its job, so the channel doesn't train
        // anyone to stop reading it.
        return outcome.Status switch
        {
            ActionOutcomeStatus.Failed => true,
            ActionOutcomeStatus.SkippedCircuitOpen => true,
            ActionOutcomeStatus.DryRun => true,
            ActionOutcomeStatus.Success => outcome.Action.Type is ActionType.ScheduledHostReboot or ActionType.ScheduledContainerReboot or ActionType.ScheduledComposeRestart,
            _ => false,
        };
    }

    private static bool TargetsAContainer(ActionType type) => type is
        ActionType.ScheduledContainerReboot or
        ActionType.CrashLoopRestart or
        ActionType.CriticalThresholdRestart or
        ActionType.PreemptiveWorstOffenderRestart;
}
