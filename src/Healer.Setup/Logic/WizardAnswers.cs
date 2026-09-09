using Healer.Core.Configuration;

namespace Healer.Setup.Logic;

/// <summary>Everything gathered across the wizard's screens, before being turned into a HealerConfig.</summary>
public sealed class WizardAnswers
{
    /// <summary>Required — every Telegram message is prefixed with it so a shared bot/chat receiving alerts from several boxes can tell them apart at a glance.</summary>
    public string ServerName { get; set; } = "";
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
    public SafetyProfile SafetyProfile { get; set; } = SafetyProfile.Balanced;
    public bool DryRun { get; set; } = true;
    public NotificationLevel NotificationLevel { get; set; } = NotificationLevel.ProblemsOnly;
    public bool HostRebootEnabled { get; set; }
    public RebootSchedule? HostReboot { get; set; }
    public List<ContainerSchedule> ContainerReboots { get; } = [];
    public bool ComposeRestartEnabled { get; set; }

    /// <summary>Projects to refresh on <see cref="ComposeRestartSchedule"/> — normally populated by
    /// picking from auto-discovered running projects (zero typing); falls back to the single
    /// manually-typed project below only when nothing could be auto-discovered.</summary>
    public List<DiscoveredComposeProject> ComposeProjects { get; set; } = [];
    public string ManualComposeProjectName { get; set; } = "";
    public string ManualComposeWorkingDirectory { get; set; } = "";
    public RebootSchedule? ComposeRestartSchedule { get; set; }
}
