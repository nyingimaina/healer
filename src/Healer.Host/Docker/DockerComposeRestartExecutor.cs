using System.Diagnostics;
using Healer.Core.Abstractions;

namespace Healer.Host.Docker;

/// <summary>
/// Shells out to the `docker` CLI's `compose` subcommand (not the Docker Engine API — compose's
/// dependency-aware restart ordering lives entirely in the CLI, there is no equivalent HTTP
/// endpoint) to restart a whole compose project at once. Requires `docker compose` (the plugin,
/// bundled with docker.io/docker-ce) to be installed on the host; Healer's own preflight check
/// already surfaces a missing/broken Docker installation before this would ever be scheduled.
/// </summary>
public sealed class DockerComposeRestartExecutor : IComposeRestartExecutor
{
    public async Task RestartProjectAsync(ComposeProjectRef project, IReadOnlyList<string> excludedServiceNames, CancellationToken ct)
    {
        var args = new List<string> { "compose", "restart" };

        // `docker compose restart` has no "all except" syntax — the only way to honor an exclusion
        // is to first ask compose for the project's actual service names and then restart every
        // OTHER one explicitly. With no exclusions, restart everything (no service args) so a
        // newly-added service in the compose file is picked up without Healer's config needing to
        // know about it by name.
        if (excludedServiceNames.Count > 0)
        {
            var services = await ListServicesAsync(project.WorkingDirectory, ct);
            var included = services.Where(s => !excludedServiceNames.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            if (included.Count == 0)
            {
                return; // every service in this project is excluded — nothing to do
            }

            args.AddRange(included);
        }

        await RunAsync(project.WorkingDirectory, args, ct);
    }

    private static async Task<List<string>> ListServicesAsync(string workingDirectory, CancellationToken ct)
    {
        var result = await RunAsync(workingDirectory, ["compose", "config", "--services"], ct);
        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string workingDirectory, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
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

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker process");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', args)} (in {workingDirectory}) failed with exit code {process.ExitCode}: {stderr}");
        }

        return (process.ExitCode, stdout, stderr);
    }
}
