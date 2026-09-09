namespace Healer.Core.Abstractions;

public sealed record PruneResult(int ItemsRemoved, long BytesReclaimed);

/// <summary>Host-level mutating actions. Implemented in Healer.Host; each requires root or CAP_SYS_ADMIN on Linux.</summary>
public interface IHostSystemActions
{
    Task<PruneResult> PruneStoppedContainersAsync(CancellationToken ct);

    Task<PruneResult> PruneDanglingImagesAsync(TimeSpan retention, CancellationToken ct);

    Task EnsureSwapfileAsync(string path, int sizeMb, CancellationToken ct);

    Task DropPageCachesAsync(CancellationToken ct);

    /// <summary>Reboots the whole host. Callers MUST persist state and notify before calling this — the process does not survive it.</summary>
    Task RebootHostAsync(CancellationToken ct);
}
