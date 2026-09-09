using Healer.Core.Configuration;

namespace Healer.Setup.Logic;

/// <summary>
/// Builds a full set of wizard answers from environment variables alone, for boxes nobody ever
/// interactively logs into — EC2 User Data / golden-AMI provisioning, "launch and forget" fleets.
/// A purely login-triggered wizard (healer-first-run.sh) can never reach that case: if nobody logs
/// in, it never fires, and the box sits unprotected forever even though `systemctl enable` would
/// happily survive every reboot once actually configured.
///
/// Always defaults to DryRun=true and the Balanced profile regardless of what's supplied — an
/// unattended, potentially fleet-wide setup must never silently start taking live remediation
/// action without a human reviewing at least one instance of it first, even when the operator has
/// supplied working Telegram credentials. A human flips it live later, the same way as any other
/// box: re-run healer-setup interactively, or edit the config directly.
/// </summary>
public static class UnattendedSetup
{
    public const string ServerNameEnvVar = "HEALER_SERVER_NAME";

    public static WizardAnswers? TryBuildFromEnvironment(Func<string, string?> getEnvVar, string fallbackServerName)
    {
        var telegramDefaults = new TelegramConfig();
        var botToken = getEnvVar(telegramDefaults.BotTokenEnvVar) ?? "";
        var chatId = getEnvVar(telegramDefaults.ChatIdEnvVar) ?? "";

        var (tokenOk, _) = TelegramFieldValidator.ValidateBotToken(botToken);
        var (chatOk, _) = TelegramFieldValidator.ValidateChatId(chatId);
        if (!tokenOk || !chatOk)
        {
            // Nothing usable to configure with — the box stays inert, exactly as if it had only
            // been staged for an eventual interactive login (see healer-first-run.sh).
            return null;
        }

        var serverName = getEnvVar(ServerNameEnvVar);
        if (string.IsNullOrWhiteSpace(serverName))
        {
            serverName = fallbackServerName;
        }

        var answers = new WizardAnswers
        {
            ServerName = serverName,
            BotToken = botToken,
            ChatId = chatId,
            SafetyProfile = SafetyProfile.Balanced,
            DryRun = true,
            NotificationLevel = NotificationLevel.ProblemsOnly,
            HostRebootEnabled = false,
        };

        return answers;
    }
}
