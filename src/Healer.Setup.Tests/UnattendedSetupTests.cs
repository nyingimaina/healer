using Healer.Core.Configuration;
using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class UnattendedSetupTests
{
    private static Func<string, string?> EnvOf(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void ReturnsNull_WhenTelegramCredentialsAreMissing()
    {
        var result = UnattendedSetup.TryBuildFromEnvironment(EnvOf(), fallbackServerName: "host-1");

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenChatIdIsInvalid()
    {
        var env = EnvOf(("TELEGRAM_BOT_TOKEN", "123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ"), ("TELEGRAM_CHAT_ID", "not-a-number"));

        var result = UnattendedSetup.TryBuildFromEnvironment(env, fallbackServerName: "host-1");

        Assert.Null(result);
    }

    [Fact]
    public void BuildsAnswers_WhenValidCredentialsArePresent()
    {
        var env = EnvOf(("TELEGRAM_BOT_TOKEN", "123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ"), ("TELEGRAM_CHAT_ID", "-1001234567890"));

        var result = UnattendedSetup.TryBuildFromEnvironment(env, fallbackServerName: "host-1");

        Assert.NotNull(result);
        Assert.Equal("123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ", result.BotToken);
        Assert.Equal("-1001234567890", result.ChatId);
    }

    [Fact]
    public void UsesFallbackServerName_WhenServerNameEnvVarIsNotSet()
    {
        var env = EnvOf(("TELEGRAM_BOT_TOKEN", "123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ"), ("TELEGRAM_CHAT_ID", "123456789"));

        var result = UnattendedSetup.TryBuildFromEnvironment(env, fallbackServerName: "ip-10-0-1-23");

        Assert.Equal("ip-10-0-1-23", result!.ServerName);
    }

    [Fact]
    public void UsesServerNameEnvVar_WhenProvided()
    {
        var env = EnvOf(
            ("TELEGRAM_BOT_TOKEN", "123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ"),
            ("TELEGRAM_CHAT_ID", "123456789"),
            (UnattendedSetup.ServerNameEnvVar, "prod-api-7"));

        var result = UnattendedSetup.TryBuildFromEnvironment(env, fallbackServerName: "ip-10-0-1-23");

        Assert.Equal("prod-api-7", result!.ServerName);
    }

    [Fact]
    public void AlwaysDefaultsToDryRunAndBalancedProfile_RegardlessOfCredentials()
    {
        var env = EnvOf(("TELEGRAM_BOT_TOKEN", "123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ"), ("TELEGRAM_CHAT_ID", "123456789"));

        var result = UnattendedSetup.TryBuildFromEnvironment(env, fallbackServerName: "host-1");

        Assert.True(result!.DryRun);
        Assert.Equal(SafetyProfile.Balanced, result.SafetyProfile);
        Assert.False(result.HostRebootEnabled);
    }

    [Fact]
    public void BuiltAnswers_ProduceAValidConfigViaTheSameBuilderTheWizardUses()
    {
        var env = EnvOf(("TELEGRAM_BOT_TOKEN", "123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ"), ("TELEGRAM_CHAT_ID", "123456789"));
        var answers = UnattendedSetup.TryBuildFromEnvironment(env, fallbackServerName: "host-1")!;

        var config = WizardConfigBuilder.Build(answers);

        Assert.Equal("host-1", config.ServerName);
        Assert.True(config.DryRun);
    }
}
