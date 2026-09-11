using Healer.Core.Abstractions;

namespace Healer.Host.EmergencyStop;

/// <summary>
/// The sentinel file IS the disabled state — no separate flag anywhere else to drift out of sync.
/// Written/read identically regardless of origin: a human via `healer-disable`/Ctrl+D in
/// healer-status, or Healer itself via Decision.EmergencyActionRateBreaker tripping. The shell
/// scripts write/read this same path and format directly (see deploy/healer-disable.sh), so any
/// format change here must stay in sync with them.
/// </summary>
public sealed class FileEmergencyStopSignal(string path) : IEmergencyStopSignal
{
    public Task<string?> GetDisabledReasonAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return Task.FromResult<string?>(null);
        }

        var content = File.ReadAllText(path).Trim();
        return Task.FromResult<string?>(content.Length == 0 ? "(no reason given)" : content);
    }

    public async Task DisableAsync(string reason, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var content = $"{reason}\n(disabled at {DateTimeOffset.UtcNow:O})\n";
        await File.WriteAllTextAsync(path, content, ct);
    }
}
