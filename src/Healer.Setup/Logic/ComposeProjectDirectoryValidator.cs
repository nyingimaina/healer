namespace Healer.Setup.Logic;

/// <summary>
/// Validates the one unavoidable typed field for the scheduled compose-restart step: the project's
/// working directory. Unlike the reboot schedule (pure menu selections, no typing at all), Healer has
/// no way to auto-discover a compose project's directory today (that would need the Docker container
/// list to start carrying `com.docker.compose.project.working_dir` labels, which it doesn't yet — see
/// docs/ARCHITECTURE.md) — so this is checked for existence and an actual compose file, the same way
/// the Telegram fields get live format validation rather than being trusted blind.
/// </summary>
public static class ComposeProjectDirectoryValidator
{
    private static readonly string[] ComposeFileNames = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    public static (bool IsValid, string? Reason) Validate(string directory)
    {
        var trimmed = directory.Trim();

        if (trimmed.Length == 0)
        {
            return (false, "Enter the folder containing this project's docker-compose file.");
        }

        if (!Directory.Exists(trimmed))
        {
            return (false, $"'{trimmed}' doesn't exist on this box.");
        }

        if (!ComposeFileNames.Any(name => File.Exists(Path.Combine(trimmed, name))))
        {
            return (false, $"No docker-compose.yml/compose.yaml found in '{trimmed}'.");
        }

        return (true, null);
    }

    /// <summary>Derives a sensible default project name from the directory's own folder name, matching how `docker compose` itself names a project when none is given explicitly.</summary>
    public static string DeriveProjectName(string directory)
    {
        var trimmed = directory.Trim().TrimEnd('/', '\\');
        return trimmed.Length == 0 ? "" : Path.GetFileName(trimmed);
    }
}
