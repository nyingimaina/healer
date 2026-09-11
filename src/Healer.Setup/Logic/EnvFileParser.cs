namespace Healer.Setup.Logic;

/// <summary>Reads a single KEY=VALUE line back out of the env file HealerInstaller.WriteEnvFileAsync
/// writes — used to recover the Telegram bot token/chat id when the wizard runs again on a box that
/// was already set up, since those secrets live only in this file, never in healer.json itself.</summary>
public static class EnvFileParser
{
    public static string? TryGetValue(string envFileContent, string key)
    {
        foreach (var line in envFileContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            if (trimmed[..separatorIndex] == key)
            {
                return trimmed[(separatorIndex + 1)..];
            }
        }

        return null;
    }
}
