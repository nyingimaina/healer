using System.Diagnostics;
using Healer.Core.Abstractions;

namespace Healer.Host.Docker;

/// <summary>
/// Shells out to Docker Compose (not the Docker Engine API — compose's dependency-aware restart
/// ordering lives entirely in the CLI, there is no equivalent HTTP endpoint) to restart a whole
/// compose project at once. Detects, on every call, whether the host has the newer `docker compose`
/// CLI plugin (v2) or only the older standalone `docker-compose` binary (v1, EOL upstream but still
/// present on many existing boxes) and uses whichever is actually available — a box with only v1
/// installed has no `docker compose` subcommand at all, so hardcoding the v2 shape would silently
/// break this feature there. Detection isn't cached: it's only invoked on a restart schedule
/// (weekly/monthly, not per-tick), so the cost of re-probing is irrelevant, and not caching means a
/// Docker/Compose upgrade takes effect without requiring a daemon restart.
/// </summary>
public sealed class DockerComposeRestartExecutor : IComposeRestartExecutor
{
    public async Task RestartProjectAsync(ComposeProjectRef project, IReadOnlyList<string> excludedServiceNames, CancellationToken ct)
    {
        var (executable, baseArgs) = await DetectComposeCommandAsync(project.WorkingDirectory, ct);

        var args = new List<string>(baseArgs) { "restart" };

        // `... restart` has no "all except" syntax — the only way to honor an exclusion is to first
        // ask compose for the project's actual service names and then restart every OTHER one
        // explicitly. With no exclusions, restart everything (no service args) so a newly-added
        // service in the compose file is picked up without Healer's config needing to know about it
        // by name.
        if (excludedServiceNames.Count > 0)
        {
            var services = await ListServicesAsync(executable, baseArgs, project.WorkingDirectory, ct);
            var included = services.Where(s => !excludedServiceNames.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            if (included.Count == 0)
            {
                return; // every service in this project is excluded — nothing to do
            }

            args.AddRange(included);
        }

        await RunAsync(executable, args, project.WorkingDirectory, ct);
    }

    /// <summary>
    /// Picks the invocation shape for whichever Docker Compose variant is actually available.
    /// Pure and `internal` specifically so it's unit-testable without mocking process execution —
    /// see <see cref="Healer.Host.Tests.DockerComposeRestartExecutorTests"/>.
    /// </summary>
    internal static (string Executable, IReadOnlyList<string> BaseArgs) ResolveComposeCommand(bool pluginAvailable, bool standaloneAvailable)
    {
        if (pluginAvailable)
        {
            return ("docker", ["compose"]);
        }

        if (standaloneAvailable)
        {
            return ("docker-compose", []);
        }

        throw new InvalidOperationException(
            "Neither the 'docker compose' plugin (v2) nor the standalone 'docker-compose' binary (v1) " +
            "is available on this host — install Docker Compose to use scheduled compose restarts.");
    }

    private static async Task<(string Executable, IReadOnlyList<string> BaseArgs)> DetectComposeCommandAsync(string workingDirectory, CancellationToken ct)
    {
        var pluginAvailable = await CommandSucceedsAsync("docker", ["compose", "version"], workingDirectory, ct);
        var standaloneAvailable = !pluginAvailable && await CommandSucceedsAsync("docker-compose", ["version"], workingDirectory, ct);
        return ResolveComposeCommand(pluginAvailable, standaloneAvailable);
    }

    private static async Task<bool> CommandSucceedsAsync(string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        try
        {
            var (exitCode, _, _) = await RunAsync(executable, args, workingDirectory, ct, throwOnNonZeroExit: false);
            return exitCode == 0;
        }
        catch (Exception)
        {
            return false; // executable not found, or failed to start — treat as "not available"
        }
    }

    private static async Task<List<string>> ListServicesAsync(string executable, IReadOnlyList<string> baseArgs, string workingDirectory, CancellationToken ct)
    {
        var args = new List<string>(baseArgs) { "config", "--services" };
        var result = await RunAsync(executable, args, workingDirectory, ct);
        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct) =>
        RunAsync(executable, args, workingDirectory, ct, throwOnNonZeroExit: true);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct, bool throwOnNonZeroExit)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {executable} process");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (throwOnNonZeroExit && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} {string.Join(' ', args)} (in {workingDirectory}) failed with exit code {process.ExitCode}: {stderr}");
        }

        return (process.ExitCode, stdout, stderr);
    }
}
