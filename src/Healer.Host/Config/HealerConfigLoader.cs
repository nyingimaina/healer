using System.Text.Json;
using Healer.Core.Configuration;
using Healer.Host.Serialization;

namespace Healer.Host.Config;

public static class HealerConfigLoader
{
    public static async Task<HealerConfig> LoadAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync(stream, HealerJsonContext.Default.HealerConfig, ct);
        return config ?? throw new InvalidOperationException($"Config file at {path} deserialized to null.");
    }

    public static async Task SaveAsync(string path, HealerConfig config, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, config, HealerJsonContext.Default.HealerConfig, ct);
    }
}
