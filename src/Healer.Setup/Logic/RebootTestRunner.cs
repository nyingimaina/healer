using System.Diagnostics;
using Healer.Host.Docker;
using Healer.Host.HostActions;
using Healer.Host.State;

namespace Healer.Setup.Logic;

/// <summary>
/// Backs the wizard's "Test Reboot Now" button. Unlike the compose-restart test, this can't just
/// call an action and show pass/fail — a reboot kills this very process before it could report
/// anything. Instead it hands off to the same PendingRebootRequestedUtc/RebootVerifier mechanism the
/// production scheduled-reboot path uses: record that a reboot was requested, then let the daemon
/// itself confirm (via Telegram + healer-status history) once it's back up, on its own.
///
/// The stop/write/start sequence below exists specifically to avoid a real race: by the time this
/// button is reachable (the Success step, after Apply), `healer` is already running. Writing the
/// marker straight into the state file while the daemon is live risks its own next periodic
/// SaveAsync — using its in-memory copy, which knows nothing of this edit — silently overwriting it
/// moments later. Stopping first, editing while nothing else can touch the file, then starting again
/// (so the daemon loads the fresh marker into memory) closes that race. Starting the service back up
/// does NOT itself advance the host's boot time — only an actual reboot does — so there's no risk of
/// a false-positive "verified" the moment it restarts.
/// </summary>
public static class RebootTestRunner
{
    public static async Task<(bool Started, string? Error)> TriggerAsync(string statePath, CancellationToken ct)
    {
        try
        {
            await RunAsync("systemctl", "stop healer", ct);

            var stateStore = new JsonFileStateStore(statePath);
            var state = await stateStore.LoadAsync(ct);
            state.PendingRebootRequestedUtc = DateTimeOffset.UtcNow;
            state.PendingRebootReason = "wizard test";
            await stateStore.SaveAsync(state, ct);

            await RunAsync("systemctl", "start healer", ct);

            if (!await HealerInstaller.IsServiceActiveAsync(ct))
            {
                return (false, "Healer didn't come back up after restarting it to prepare for the test — check `systemctl status healer` before trying again.");
            }

            // Reused directly from Healer.Host — DockerApiClient is unused by RebootHostAsync itself,
            // but required by LinuxHostSystemActions's constructor shape.
            await new LinuxHostSystemActions(new DockerApiClient("/var/run/docker.sock")).RebootHostAsync(ct);
            return (true, null); // unreachable in practice — the process dies with the reboot it just triggered
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start process: {fileName} {arguments}");

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} exited with code {process.ExitCode}: {stderr}");
        }
    }
}
