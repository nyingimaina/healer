using Healer.Core.Configuration;

namespace Healer.Setup.Logic;

/// <summary>Turns the wizard's gathered answers into the final HealerConfig — the only place the two meet, so it's the one thing worth unit-testing end-to-end for the wizard's output.</summary>
public static class WizardConfigBuilder
{
    public static HealerConfig Build(WizardAnswers answers)
    {
        var baseConfig = new HealerConfig
        {
            ServerName = answers.ServerName,
            DryRun = answers.DryRun,
            NotificationLevel = answers.NotificationLevel,
            ScheduledReboots = new ScheduledRebootsConfig
            {
                Host = answers.HostRebootEnabled && answers.HostReboot is { } host ? host : RebootSchedule.Disabled(),
                Containers = answers.ContainerReboots,
            },
            ScheduledComposeRestarts = new ScheduledComposeRestartsConfig
            {
                Projects = answers.ComposeRestartEnabled && answers.ComposeRestartSchedule is { } composeSchedule
                    ? ResolveComposeProjects(answers).Select(p => new ComposeProjectSchedule { ProjectName = p.Name, WorkingDirectory = p.WorkingDirectory, Schedule = composeSchedule }).ToList()
                    : [],
            },
        };

        return SafetyProfilePresets.ApplyTo(baseConfig, answers.SafetyProfile);
    }

    /// <summary>Picked-from-a-checklist projects take priority; the single manually-typed project is only used as a fallback when nothing was auto-discovered.</summary>
    private static IReadOnlyList<DiscoveredComposeProject> ResolveComposeProjects(WizardAnswers answers)
    {
        if (answers.ComposeProjects.Count > 0)
        {
            return answers.ComposeProjects;
        }

        return answers.ManualComposeProjectName.Length > 0 && answers.ManualComposeWorkingDirectory.Length > 0
            ? [new DiscoveredComposeProject(answers.ManualComposeProjectName, answers.ManualComposeWorkingDirectory)]
            : [];
    }
}
