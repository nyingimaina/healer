using Healer.Core.Configuration;
using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class WizardAnswersLoaderTests
{
    [Fact]
    public void Populate_CopiesTheSimpleScalarFieldsFromAnExistingConfig()
    {
        var config = new HealerConfig
        {
            ServerName = "prod-api-1",
            DryRun = false,
            NotificationLevel = NotificationLevel.Everything,
        };
        var answers = new WizardAnswers();

        WizardAnswersLoader.Populate(answers, config, botToken: "tok123", chatId: "chat456");

        Assert.Equal("prod-api-1", answers.ServerName);
        Assert.False(answers.DryRun);
        Assert.Equal(NotificationLevel.Everything, answers.NotificationLevel);
        Assert.Equal("tok123", answers.BotToken);
        Assert.Equal("chat456", answers.ChatId);
    }

    [Fact]
    public void Populate_LeavesTelegramFieldsEmptyWhenTheEnvFileHadNoValues()
    {
        var config = new HealerConfig { ServerName = "box" };
        var answers = new WizardAnswers();

        WizardAnswersLoader.Populate(answers, config, botToken: null, chatId: null);

        Assert.Equal("", answers.BotToken);
        Assert.Equal("", answers.ChatId);
    }

    [Fact]
    public void Populate_CarriesOverAnEnabledHostRebootSchedule()
    {
        var schedule = new RebootSchedule { Enabled = true, IntervalDays = 14, AnchorDate = new DateOnly(2026, 1, 1), Hour = 2 };
        var config = new HealerConfig
        {
            ServerName = "box",
            ScheduledReboots = new ScheduledRebootsConfig { Host = schedule },
        };
        var answers = new WizardAnswers();

        WizardAnswersLoader.Populate(answers, config, botToken: null, chatId: null);

        Assert.True(answers.HostRebootEnabled);
        Assert.Equal(14, answers.HostReboot?.IntervalDays);
        Assert.Equal(2, answers.HostReboot?.Hour);
    }

    [Fact]
    public void Populate_LeavesHostRebootDisabledWhenTheSavedScheduleWasDisabled()
    {
        var config = new HealerConfig { ServerName = "box" }; // default ScheduledReboots.Host is RebootSchedule.Disabled()
        var answers = new WizardAnswers();

        WizardAnswersLoader.Populate(answers, config, botToken: null, chatId: null);

        Assert.False(answers.HostRebootEnabled);
        Assert.Null(answers.HostReboot);
    }

    [Fact]
    public void Populate_CarriesOverTheFirstSavedComposeProjectAsTheManualFallback()
    {
        var schedule = new RebootSchedule { Enabled = true, IntervalDays = 7, AnchorDate = new DateOnly(2026, 1, 1), Hour = 3 };
        var config = new HealerConfig
        {
            ServerName = "box",
            ScheduledComposeRestarts = new ScheduledComposeRestartsConfig
            {
                Projects =
                [
                    new ComposeProjectSchedule { ProjectName = "billing", WorkingDirectory = "/srv/billing", Schedule = schedule },
                ],
            },
        };
        var answers = new WizardAnswers();

        WizardAnswersLoader.Populate(answers, config, botToken: null, chatId: null);

        Assert.True(answers.ComposeRestartEnabled);
        Assert.Equal("billing", answers.ManualComposeProjectName);
        Assert.Equal("/srv/billing", answers.ManualComposeWorkingDirectory);
        Assert.Equal(7, answers.ComposeRestartSchedule?.IntervalDays);
    }

    [Fact]
    public void Populate_LeavesComposeRestartDisabledWhenNoneWereSaved()
    {
        var config = new HealerConfig { ServerName = "box" };
        var answers = new WizardAnswers();

        WizardAnswersLoader.Populate(answers, config, botToken: null, chatId: null);

        Assert.False(answers.ComposeRestartEnabled);
        Assert.Equal("", answers.ManualComposeProjectName);
    }
}
