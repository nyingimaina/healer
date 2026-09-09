using System.Runtime.InteropServices;

namespace Healer.Setup.Logic;

public sealed record PreflightCheckResult(string Name, bool Passed, string DetailIfFailed);

/// <summary>
/// Auto-detects everything the wizard would otherwise have to ask a non-technical person about.
/// Takes the paths/probes as parameters so it's testable without a real Docker socket or systemd.
/// </summary>
public static class PreflightChecker
{
    public static IReadOnlyList<PreflightCheckResult> RunAll(
        string dockerSocketPath = "/var/run/docker.sock",
        Func<string, bool>? pathExists = null,
        Func<string, bool>? commandExists = null,
        long minFreeDiskBytes = 500L * 1024 * 1024)
    {
        pathExists ??= Path.Exists;
        commandExists ??= DefaultCommandExists;

        var results = new List<PreflightCheckResult>
        {
            new(
                "Running as root",
                Passed: IsEffectivelyRoot(),
                DetailIfFailed: "Healer needs root to manage Docker, swap, and reboots. Re-run this wizard with sudo."),

            new(
                "Docker is running",
                Passed: pathExists(dockerSocketPath),
                DetailIfFailed: $"Couldn't find {dockerSocketPath}. Start Docker (e.g. `sudo systemctl start docker`) and re-run this wizard."),

            new(
                "systemd is available",
                Passed: commandExists("systemctl"),
                DetailIfFailed: "This box doesn't appear to use systemd, which Healer's daemon relies on to run reliably in the background."),

            new(
                "Supported CPU architecture",
                Passed: RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64,
                DetailIfFailed: $"Detected {RuntimeInformation.ProcessArchitecture}, but Healer only ships linux-x64 and linux-arm64 builds."),
        };

        try
        {
            var free = new DriveInfo("/").AvailableFreeSpace;
            results.Add(new PreflightCheckResult(
                "Enough free disk space",
                Passed: free >= minFreeDiskBytes,
                DetailIfFailed: $"Only {free / 1024 / 1024}MB free on / — Healer's history database and logs need some headroom to work with."));
        }
        catch (Exception)
        {
            results.Add(new PreflightCheckResult("Enough free disk space", Passed: true, DetailIfFailed: "")); // couldn't check — don't block setup over it
        }

        return results;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static bool IsEffectivelyRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return true; // not applicable off-Linux (e.g. running this check during dev/tests on Windows) — don't block on it
        }

        try
        {
            return geteuid() == 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool DefaultCommandExists(string command)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        return pathVar.Split(Path.PathSeparator).Any(dir => Path.Exists(Path.Combine(dir, command)));
    }
}
