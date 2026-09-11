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
        await MaybeVerifyPendingRebootAsync(host, state, now, ct);

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
                await NotifyAndRecordAsync(skipped, state, ct);
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
            await NotifyAndRecordAsync(dryRunOutcome, state, ct);
            return;
        }

        if (action.Type == ActionType.ScheduledHostReboot)
        {
            // Persist state and notify BEFORE rebooting — the process does not survive to report afterward.
            // PendingRebootRequestedUtc/Reason let a LATER tick (after the daemon restarts) confirm via
            // RebootVerifier whether the reboot actually happened, rather than just optimistically
            // trusting the Success outcome recorded below.
            state.LastScheduledHostRebootUtc = now;
            state.PendingRebootRequestedUtc = now;
            state.PendingRebootReason = "scheduled reboot window";
            state.LastGlobalActionUtc = now;
            await _stateStore.SaveAsync(state, ct);

            var rebootOutcome = new ActionOutcome { Action = action, Status = ActionOutcomeStatus.Success, TimestampUtc = now };
            await NotifyAndRecordAsync(rebootOutcome, state, ct);

            await _hostSystemActions.RebootHostAsync(ct);
            return;
        }

        var outcome = await ExecuteLiveActionAsync(action, state, now, ct);
        await NotifyAndRecordAsync(outcome, state, ct);
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
            await NotifyAndRecordAsync(verification, state, ct);
        }
    }

    private async Task MaybeVerifyPendingRebootAsync(HostMetrics host, HealerState state, DateTimeOffset now, CancellationToken ct)
    {
        var verification = RebootVerifier.Evaluate(state.PendingRebootRequestedUtc, host.BootTimeUtc, now);
        if (verification is RebootVerificationOutcome.NothingPending or RebootVerificationOutcome.StillWaiting)
        {
            return;
        }

        var action = new PlannedAction { Type = ActionType.HostRebootVerification, Target = "host", Reason = state.PendingRebootReason ?? "unknown" };
        var outcome = verification == RebootVerificationOutcome.Verified
            ? new ActionOutcome { Action = action, Status = ActionOutcomeStatus.Success, TimestampUtc = now }
            : new ActionOutcome
            {
                Action = action,
                Status = ActionOutcomeStatus.Failed,
                TimestampUtc = now,
                Detail = "host boot time never advanced past the request within the grace window — the host may not have rebooted, or Healer didn't come back",
            };

        await NotifyAndRecordAsync(outcome, state, ct);

        state.PendingRebootRequestedUtc = null;
        state.PendingRebootReason = null;
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

    /// <summary>Scheduled/planned action types eligible for the capped-exponential-backoff success
    /// notification throttle — see <see cref="ScheduledSuccessNotificationGate"/>. Reactive types
    /// (crash-loop, worst-offender, etc.) are never in this list; their successes already stay
    /// silent under ProblemsOnly regardless, unaffected by this feature.</summary>
    private static readonly ActionType[] ScheduledActionTypesEligibleForSuccessBackoff =
    [
        ActionType.ScheduledHostReboot,
        ActionType.ScheduledContainerReboot,
        ActionType.ScheduledComposeRestart,
        ActionType.HostRebootVerification,
    ];

    private async Task NotifyAndRecordAsync(ActionOutcome outcome, HealerState state, CancellationToken ct)
    {
        // History always gets everything, unaffected by the Telegram noise preference — it's the
        // durable audit trail (browsable via Healer.Status), separate from what interrupts a phone.
        if (ShouldSendToTelegram(outcome, state))
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

    private bool ShouldSendToTelegram(ActionOutcome outcome, HealerState state)
    {
        if (_config.NotificationLevel == NotificationLevel.Everything)
        {
            return true;
        }

        var isEligibleForBackoff = ScheduledActionTypesEligibleForSuccessBackoff.Contains(outcome.Action.Type);

        if (isEligibleForBackoff && outcome.Status == ActionOutcomeStatus.Success && _config.ScheduledSuccessBackoff.Enabled)
        {
            return EvaluateScheduledSuccessBackoff(outcome.Action, state);
        }

        if (isEligibleForBackoff && outcome.Status != ActionOutcomeStatus.Success)
        {
            // Any non-success outcome for one of these types (Failed, SkippedCircuitOpen, DryRun) —
            // "the first error message" — resets the backoff so the run right after a real problem
            // notifies immediately, at full frequency, not still-possibly-suppressed. Deliberately
            // does NOT change the notify decision itself, which still goes through the untouched
            // switch below — only whether this key's streak survives.
            var key = BackoffKey(outcome.Action);
            state.ScheduledSuccessNotify.Remove(key);
        }

        // ProblemsOnly: always surface actual problems and disruptive planned events; stay quiet
        // about Healer routinely and successfully doing its job, so the channel doesn't train
        // anyone to stop reading it.
        return outcome.Status switch
        {
            ActionOutcomeStatus.Failed => true,
            ActionOutcomeStatus.SkippedCircuitOpen => true,
            ActionOutcomeStatus.DryRun => true,
            ActionOutcomeStatus.Success when isEligibleForBackoff => true, // backoff disabled (checked above) — old always-notify behavior
            _ => false,
        };
    }

    private bool EvaluateScheduledSuccessBackoff(PlannedAction action, HealerState state)
    {
        var key = BackoffKey(action);
        var current = state.ScheduledSuccessNotify.TryGetValue(key, out var existing) ? existing : new ScheduledActionNotifyState();

        var (shouldNotify, successesSinceLastNotify, skipThreshold) = ScheduledSuccessNotificationGate.Evaluate(
            current.SuccessesSinceLastNotify, current.SkipThreshold, _config.ScheduledSuccessBackoff.MaxSkip);

        state.ScheduledSuccessNotify[key] = new ScheduledActionNotifyState
        {
            SuccessesSinceLastNotify = successesSinceLastNotify,
            SkipThreshold = skipThreshold,
        };

        return shouldNotify;
    }

    private static string BackoffKey(PlannedAction action) => $"{action.Type}:{action.Target}";

    private static bool TargetsAContainer(ActionType type) => type is
        ActionType.ScheduledContainerReboot or
        ActionType.CrashLoopRestart or
        ActionType.CriticalThresholdRestart or
        ActionType.PreemptiveWorstOffenderRestart;
}
