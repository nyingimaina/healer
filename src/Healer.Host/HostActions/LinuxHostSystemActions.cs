using System.Diagnostics;
using Healer.Core.Abstractions;
using Healer.Host.Docker;
using Serilog;

namespace Healer.Host.HostActions;

/// <summary>
/// Host-level mutating actions. Prune goes through the Docker Engine API (via the shared
/// DockerApiClient); swapfile/drop-caches/reboot are genuine host OS actions requiring root.
/// Every action logs its own outcome — this is the OS-level detail (exact exit codes/stderr)
/// beyond what HealingEngine records as an ActionOutcome.
/// </summary>
public sealed class LinuxHostSystemActions(DockerApiClient dockerApi) : IHostSystemActions
{
    public async Task<PruneResult> PruneStoppedContainersAsync(CancellationToken ct)
    {
        var (count, bytes) = await dockerApi.PruneStoppedContainersAsync(ct);
        Log.Information("Pruned {Count} stopped container(s), reclaiming {Bytes} bytes", count, bytes);
        return new PruneResult(count, bytes);
    }

    public async Task<PruneResult> PruneDanglingImagesAsync(TimeSpan retention, CancellationToken ct)
    {
        var (count, bytes) = await dockerApi.PruneDanglingImagesAsync(retention, ct);
        Log.Information("Pruned {Count} dangling image(s) older than {RetentionHours}h, reclaiming {Bytes} bytes", count, retention.TotalHours, bytes);
        return new PruneResult(count, bytes);
    }

    public async Task EnsureSwapfileAsync(string path, int sizeMb, CancellationToken ct)
    {
        if (await IsSwapfileActiveAsync(path, ct))
        {
            Log.Debug("Swapfile {Path} already active, nothing to do", path);
            return;
        }

        Log.Information("Creating {SizeMb}MB swapfile at {Path}", sizeMb, path);
        await RunAsync("fallocate", $"-l {sizeMb}M \"{path}\"", ct);
        await RunAsync("chmod", $"600 \"{path}\"", ct);
        await RunAsync("mkswap", $"\"{path}\"", ct);
        await RunAsync("swapon", $"\"{path}\"", ct);
        Log.Information("Swapfile {Path} is now active", path);
    }

    public Task DropPageCachesAsync(CancellationToken ct)
    {
        Log.Information("Dropping page caches");
        // Writing directly to the proc file avoids spawning a shell just to run `echo 1 > ...`.
        return File.WriteAllTextAsync("/proc/sys/vm/drop_caches", "1", ct);
    }

    public Task RebootHostAsync(CancellationToken ct)
    {
        Log.Warning("Rebooting the host now");
        return RunAsync("systemctl", "reboot", ct);
    }

    private static async Task<bool> IsSwapfileActiveAsync(string path, CancellationToken ct)
    {
        if (!File.Exists("/proc/swaps"))
        {
            return false;
        }

        var lines = await File.ReadAllLinesAsync("/proc/swaps", ct);
        return lines.Any(line => line.StartsWith(path, StringComparison.Ordinal));
    }

    private static async Task RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start process: {fileName} {arguments}");

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            Log.Error("{FileName} {Arguments} exited with code {ExitCode}: {StdErr}", fileName, arguments, process.ExitCode, stderr);
            throw new InvalidOperationException($"{fileName} {arguments} exited with code {process.ExitCode}: {stderr}");
        }
    }
}
