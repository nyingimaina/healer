using Healer.Core.Configuration;

namespace Healer.Setup.Logic;

/// <summary>
/// Pre-fills WizardAnswers from a previously-saved HealerConfig (+ recovered Telegram secrets), so
/// re-running the setup wizard on an already-configured box doesn't force retyping everything —
/// see Program.cs's startup, which loads healer.json/healer.env before building the wizard's steps.
///
/// Not covered: SafetyProfile isn't itself persisted anywhere in HealerConfig (only the individual
/// settings it derives are saved), so there's no way to reliably reconstruct which profile was
/// originally chosen — it always starts back at the "Balanced (recommended)" default.
/// </summary>
public static class WizardAnswersLoader
{
    public static void Populate(WizardAnswers answers, HealerConfig config, string? botToken, string? chatId)
    {
        answers.ServerName = config.ServerName;
        answers.DryRun = config.DryRun;
        answers.NotificationLevel = config.NotificationLevel;

        if (botToken is not null)
        {
            answers.BotToken = botToken;
        }

        if (chatId is not null)
        {
            answers.ChatId = chatId;
        }

        if (config.ScheduledReboots.Host.Enabled)
        {
            answers.HostRebootEnabled = true;
            answers.HostReboot = config.ScheduledReboots.Host;
        }

        var firstComposeProject = config.ScheduledComposeRestarts.Projects.FirstOrDefault();
        if (firstComposeProject is not null)
        {
            answers.ComposeRestartEnabled = true;
            answers.ComposeRestartSchedule = firstComposeProject.Schedule;
            answers.ManualComposeProjectName = firstComposeProject.ProjectName;
            answers.ManualComposeWorkingDirectory = firstComposeProject.WorkingDirectory;
        }
    }
}
