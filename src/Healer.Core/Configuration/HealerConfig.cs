namespace Healer.Core.Configuration;

// IMPORTANT — why these are positional records, not `{ get; init; } = value` properties:
// System.Text.Json's SOURCE-GENERATED (de)serializer — required for Native AOT/trim safety, see
// HealerJsonContext — silently discards a property's C# initializer default whenever that property
// is absent from the JSON being deserialized, for ordinary `{ get; init; } = value` properties. A
// hand-edited or partially-outdated healer.json omitting any field would then silently zero/null it
// out rather than falling back to the documented default (e.g. a missing hostMemoryCriticalPercent
// silently becoming 0.0, which is a live safety hazard for a threshold-driven system like this one).
// Positional records with constructor-parameter defaults do NOT have this problem — the source
// generator correctly falls back to the parameter's default value for anything missing. Verified
// empirically (a nested-object property, a scalar, and a list all round-tripped correctly this way;
// none did as `{ get; init; } = value`) before adopting this pattern everywhere in this file.
// Reference-type defaults that aren't compile-time constants (a nested config's `new()`, a `[]]`
// list literal, `TimeZoneInfo.Local.Id`) use a nullable positional parameter plus a body-redeclared
// property computing the real default from it — the same pattern proven above.

public enum NotificationLevel
{
    /// <summary>Every incident and outcome, including routine successful remediations — useful while evaluating a box in dry-run and building trust in Healer's judgement.</summary>
    Everything,

    /// <summary>
    /// Only what actually needs attention: failures, a circuit opening (repeated failures — Healer has
    /// stopped auto-restarting and needs a human), dry-run previews (they represent a real incident,
    /// even though nothing was actually done), and host/container reboots (disruptive enough to always
    /// mention). Healer routinely and successfully restarting something stays silent on Telegram — it's
    /// still recorded in history (see Healer.Status) — specifically so the channel doesn't fill with
    /// "fixed it" pings that train you to stop reading it, which defeats the point of alerting at all.
    /// </summary>
    ProblemsOnly,
}

public sealed record ThresholdsConfig(
    double HostMemoryWarningPercent = 80,
    double HostMemoryCriticalPercent = 90,
    double HostSwapCriticalPercent = 90,
    double DiskWarningPercent = 80,
    double DiskCriticalPercent = 90,
    IReadOnlyList<string>? DiskMountsToCheck = null,
    double LoadAvg1CriticalMultiplierOfCores = 4.0,
    double ContainerMemoryWarningPercentOfLimit = 85,
    double ContainerMemoryCriticalPercentOfLimit = 95,
    double ContainerMemoryWarningPercentOfHostWhenNoLimit = 15,
    double ContainerMemoryCriticalPercentOfHostWhenNoLimit = 25,
    int UnhealthyGraceSeconds = 60,
    int SustainedBreachTicksRequired = 2)
{
    /// <summary>Fallback thresholds (percent of TOTAL HOST memory) used when a container has no mem_limit set at all.</summary>
    public IReadOnlyList<string> DiskMountsToCheck { get; init; } = DiskMountsToCheck ?? ["/", "/var/lib/docker"];
}

public sealed record RestartPolicyConfig(
    IReadOnlyList<int>? BackoffStagesSeconds = null,
    int CircuitBreakerFailureThreshold = 5,
    int CircuitBreakerWindowMinutes = 30,
    int CircuitBreakerCooldownMinutes = 60,
    int GlobalActionCooldownSeconds = 90,
    int MaxConcurrentActionsPerTick = 1,
    int RestartVerificationGraceSeconds = 60)
{
    public IReadOnlyList<int> BackoffStagesSeconds { get; init; } = BackoffStagesSeconds ?? [30, 60, 120, 300, 900];
}

public sealed record HostPressureReliefConfig(
    bool EnablePruneStoppedContainers = true,
    bool EnablePruneDanglingImages = true,
    int PruneImageRetentionHours = 24,
    bool EnableSwapfile = false,
    string SwapfilePath = "/swapfile",
    int SwapfileSizeMb = 2048,
    bool EnableDropCaches = false);

public sealed record ContainerOverrideConfig(
    string NamePattern = "",
    bool ExcludeFromWorstOffenderSelection = false,
    double? ContainerMemoryCriticalPercentOfLimit = null,
    bool ExcludeFromPeriodicComposeRestart = false)
{
    public required string NamePattern { get; init; } = NamePattern;
}

public sealed record TelegramConfig(
    string BotTokenEnvVar = "TELEGRAM_BOT_TOKEN",
    string ChatIdEnvVar = "TELEGRAM_CHAT_ID");

public sealed record HistoryConfig(
    string HistoryDbPath = "/var/lib/healer/history.db",
    int SnapshotIntervalSeconds = 60,
    int ResourceSnapshotRetentionDays = 15,
    int ActionHistoryRetentionDays = 90,
    int RetentionSweepHour = 3);

/// <summary>
/// Instrumentation logging via Serilog, with the same "keep disk usage low on a constrained box"
/// philosophy already applied to the SQLite history retention: a size-capped, count-capped rolling
/// file, never an unbounded log that can fill the disk on its own. Also readable from Healer.Status.
/// </summary>
public sealed record LoggingConfig(
    string LogDirectory = "/var/log/healer",
    int FileSizeLimitMb = 10,
    int RetainedFileCount = 7,
    string MinimumLevel = "Information");

/// <summary>
/// Capped exponential backoff on repeated-SUCCESS Telegram notifications for scheduled host reboots
/// and compose restarts — see <see cref="Decision.ScheduledSuccessNotificationGate"/>. Set
/// <see cref="Enabled"/> to false to restore the old "always notify on every successful scheduled
/// run" behavior exactly.
/// </summary>
public sealed record ScheduledSuccessBackoffConfig(
    bool Enabled = true,
    int MaxSkip = 16);

/// <summary>
/// Last-resort, engine-wide tripwire — see <see cref="Decision.EmergencyActionRateBreaker"/>.
/// Deliberately set <see cref="MaxActionsInWindow"/>/<see cref="WindowMinutes"/> well above what
/// <see cref="RestartPolicyConfig.GlobalActionCooldownSeconds"/> should ever physically allow (e.g.
/// the 90s default cooldown caps out around 10 actions/15min) — this should never fire under any
/// legitimate operation, however aggressive, only when something is bypassing Healer's own
/// throttles. Set <see cref="Enabled"/> to false to turn this off entirely.
/// </summary>
public sealed record EmergencyBreakerConfig(
    bool Enabled = true,
    int MaxActionsInWindow = 15,
    int WindowMinutes = 15);

public sealed record ScheduledRebootsConfig(
    RebootSchedule? Host = null,
    IReadOnlyList<ContainerSchedule>? Containers = null)
{
    public RebootSchedule Host { get; init; } = Host ?? RebootSchedule.Disabled();
    public IReadOnlyList<ContainerSchedule> Containers { get; init; } = Containers ?? [];
}

public sealed record HealerConfig(
    string ServerName = "",
    bool DryRun = true,
    int PollIntervalSeconds = 15,
    NotificationLevel NotificationLevel = NotificationLevel.ProblemsOnly,
    string DockerSocketPath = "/var/run/docker.sock",
    string StatePath = "/var/lib/healer/state.json",
    TelegramConfig? Telegram = null,
    ThresholdsConfig? Thresholds = null,
    RestartPolicyConfig? RestartPolicy = null,
    HostPressureReliefConfig? HostPressureRelief = null,
    ScheduledRebootsConfig? ScheduledReboots = null,
    ScheduledComposeRestartsConfig? ScheduledComposeRestarts = null,
    ScheduledSuccessBackoffConfig? ScheduledSuccessBackoff = null,
    EmergencyBreakerConfig? EmergencyBreaker = null,
    IReadOnlyList<ContainerOverrideConfig>? ContainerOverrides = null,
    HistoryConfig? History = null,
    LoggingConfig? Logging = null)
{
    /// <summary>
    /// A short human name for this box (e.g. "prod-api-1"), required at setup time. Every Telegram
    /// message is prefixed with it — with one bot/chat potentially receiving alerts from several
    /// boxes, "container X is unhealthy" is useless without knowing which server sent it.
    /// </summary>
    public required string ServerName { get; init; } = ServerName;

    public TelegramConfig Telegram { get; init; } = Telegram ?? new();
    public ThresholdsConfig Thresholds { get; init; } = Thresholds ?? new();
    public RestartPolicyConfig RestartPolicy { get; init; } = RestartPolicy ?? new();
    public HostPressureReliefConfig HostPressureRelief { get; init; } = HostPressureRelief ?? new();
    public ScheduledRebootsConfig ScheduledReboots { get; init; } = ScheduledReboots ?? new();
    public ScheduledComposeRestartsConfig ScheduledComposeRestarts { get; init; } = ScheduledComposeRestarts ?? new();
    public ScheduledSuccessBackoffConfig ScheduledSuccessBackoff { get; init; } = ScheduledSuccessBackoff ?? new();
    public EmergencyBreakerConfig EmergencyBreaker { get; init; } = EmergencyBreaker ?? new();
    public IReadOnlyList<ContainerOverrideConfig> ContainerOverrides { get; init; } = ContainerOverrides ?? [];
    public HistoryConfig History { get; init; } = History ?? new();
    public LoggingConfig Logging { get; init; } = Logging ?? new();
}
