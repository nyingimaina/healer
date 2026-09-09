using System.Text.Json;
using Healer.Core.Abstractions;
using Healer.Core.Models;
using Healer.Host.Serialization;
using Serilog;

namespace Healer.Host.State;

/// <summary>Persists HealerState as JSON, written atomically (temp file + rename) so a crash or reboot mid-write can never leave a corrupt/partial state file behind.</summary>
public sealed class JsonFileStateStore(string path) : IStateStore
{
    public async Task<HealerState> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new HealerState();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync(stream, HealerJsonContext.Default.HealerState, ct);
            return state ?? new HealerState();
        }
        catch (JsonException ex)
        {
            // A corrupt state file must never prevent the daemon from starting — worst case we lose
            // backoff/circuit history and start fresh, which is safe (just more cautious than ideal).
            // Still worth a loud log line: this is exactly the kind of thing you'd want to know about.
            Log.Warning(ex, "State file at {Path} is corrupt or unreadable — starting with fresh state", path);
            return new HealerState();
        }
    }

    public async Task SaveAsync(HealerState state, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, HealerJsonContext.Default.HealerState, ct);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
