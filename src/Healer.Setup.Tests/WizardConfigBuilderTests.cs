using Healer.Core.Configuration;
using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class WizardConfigBuilderTests
{
    [Theory]
    [InlineData(SafetyProfile.Conservative)]
    [InlineData(SafetyProfile.Balanced)]
    [InlineData(SafetyProfile.Aggressive)]
    public void Build_MapsSafetyProfileToMatchingConcreteConfig(SafetyProfile profile)
    {
        var answers = new WizardAnswers { SafetyProfile = profile, DryRun = true };

        var config = WizardConfigBuilder.Build(answers);

        var expected = SafetyProfilePresets.ApplyTo(new HealerConfig { ServerName = "test-server", DryRun = true }, profile);
        Assert.Equal(expected.Thresholds.HostMemoryCriticalPercent, config.Thresholds.HostMemoryCriticalPercent);
        Assert.Equal(expected.RestartPolicy.CircuitBreakerFailureThreshold, config.RestartPolicy.CircuitBreakerFailureThreshold);
    }

    [Fact]
    public void Build_CarriesServerNameThrough()
    {
        var answers = new WizardAnswers { ServerName = "prod-api-1" };

        var config = WizardConfigBuilder.Build(answers);

        Assert.Equal("prod-api-1", config.ServerName);
    }

    [Fact]
    public void Build_CarriesDryRunAndNotificationLevelThrough()
    {
        var answers = new WizardAnswers { DryRun = false, NotificationLevel = NotificationLevel.Everything };

        var config = WizardConfigBuilder.Build(answers);

        Assert.False(config.DryRun);
        Assert.Equal(NotificationLevel.Everything, config.NotificationLevel);
    }

    [Fact]
    public void Build_DisabledHostReboot_WhenNotEnabled()
    {
        var answers = new WizardAnswers { HostRebootEnabled = false };

        var config = WizardConfigBuilder.Build(answers);

        Assert.False(config.ScheduledReboots.Host.Enabled);
    }

    [Fact]
    public void Build_UsesProvidedHostRebootSchedule_WhenEnabled()
    {
        var schedule = RebootScheduleOptions.Build(intervalDays: 7, hour: 3, minute: 0, weeklyDayOfWeek: DayOfWeek.Sunday, timeZoneId: "UTC", nowUtc: DateTimeOffset.UtcNow);
        var answers = new WizardAnswers { HostRebootEnabled = true, HostReboot = schedule };

        var config = WizardConfigBuilder.Build(answers);

        Assert.True(config.ScheduledReboots.Host.Enabled);
        Assert.Equal(7, config.ScheduledReboots.Host.IntervalDays);
    }

    [Fact]
    public void Build_NoComposeProjects_WhenNotEnabled()
    {
        var answers = new WizardAnswers { ComposeRestartEnabled = false };

        var config = WizardConfigBuilder.Build(answers);

        Assert.Empty(config.ScheduledComposeRestarts.Projects);
    }

    [Fact]
    public void Build_UsesDiscoveredProjects_WhenAnyWerePicked()
    {
        var schedule = RebootScheduleOptions.Build(intervalDays: 7, hour: 4, minute: 0, weeklyDayOfWeek: DayOfWeek.Sunday, timeZoneId: "UTC", nowUtc: DateTimeOffset.UtcNow);
        var answers = new WizardAnswers
        {
            ComposeRestartEnabled = true,
            ComposeProjects = [new DiscoveredComposeProject("milele", "/opt/milele"), new DiscoveredComposeProject("skips", "/opt/skips")],
            ComposeRestartSchedule = schedule,
        };

        var config = WizardConfigBuilder.Build(answers);

        Assert.Equal(2, config.ScheduledComposeRestarts.Projects.Count);
        Assert.Contains(config.ScheduledComposeRestarts.Projects, p => p.ProjectName == "milele" && p.WorkingDirectory == "/opt/milele");
        Assert.Contains(config.ScheduledComposeRestarts.Projects, p => p.ProjectName == "skips" && p.WorkingDirectory == "/opt/skips");
        Assert.All(config.ScheduledComposeRestarts.Projects, p => Assert.Equal(7, p.Schedule.IntervalDays));
    }

    [Fact]
    public void Build_FallsBackToManualProject_WhenNothingWasDiscovered()
    {
        var schedule = RebootScheduleOptions.Build(intervalDays: 7, hour: 4, minute: 0, weeklyDayOfWeek: DayOfWeek.Sunday, timeZoneId: "UTC", nowUtc: DateTimeOffset.UtcNow);
        var answers = new WizardAnswers
        {
            ComposeRestartEnabled = true,
            ManualComposeProjectName = "milele",
            ManualComposeWorkingDirectory = "/opt/milele",
            ComposeRestartSchedule = schedule,
        };

        var config = WizardConfigBuilder.Build(answers);

        var project = Assert.Single(config.ScheduledComposeRestarts.Projects);
        Assert.Equal("milele", project.ProjectName);
        Assert.Equal("/opt/milele", project.WorkingDirectory);
        Assert.True(project.Schedule.Enabled);
        Assert.Equal(7, project.Schedule.IntervalDays);
    }

    [Fact]
    public void Build_PrefersDiscoveredProjects_OverManualFallback_WhenBothPresent()
    {
        var schedule = RebootScheduleOptions.Build(intervalDays: 7, hour: 4, minute: 0, weeklyDayOfWeek: DayOfWeek.Sunday, timeZoneId: "UTC", nowUtc: DateTimeOffset.UtcNow);
        var answers = new WizardAnswers
        {
            ComposeRestartEnabled = true,
            ComposeProjects = [new DiscoveredComposeProject("milele", "/opt/milele")],
            ManualComposeProjectName = "stale-leftover",
            ManualComposeWorkingDirectory = "/opt/stale",
            ComposeRestartSchedule = schedule,
        };

        var config = WizardConfigBuilder.Build(answers);

        var project = Assert.Single(config.ScheduledComposeRestarts.Projects);
        Assert.Equal("milele", project.ProjectName);
    }
}
