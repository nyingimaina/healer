using Healer.Core.Abstractions;

namespace Healer.Tests.Fakes;

public sealed class FakeHostSystemActions : IHostSystemActions
{
    public int PruneStoppedContainersCalls { get; private set; }
    public int PruneDanglingImagesCalls { get; private set; }
    public int EnsureSwapfileCalls { get; private set; }
    public int DropPageCachesCalls { get; private set; }
    public int RebootHostCalls { get; private set; }

    /// <summary>Invoked synchronously inside RebootHostAsync, before it returns — lets a test capture other fakes' state at that instant to verify save/notify happened first.</summary>
    public Action? OnRebootHost { get; set; }

    public Task<PruneResult> PruneStoppedContainersAsync(CancellationToken ct)
    {
        PruneStoppedContainersCalls++;
        return Task.FromResult(new PruneResult(0, 0));
    }

    public Task<PruneResult> PruneDanglingImagesAsync(TimeSpan retention, CancellationToken ct)
    {
        PruneDanglingImagesCalls++;
        return Task.FromResult(new PruneResult(0, 0));
    }

    public Task EnsureSwapfileAsync(string path, int sizeMb, CancellationToken ct)
    {
        EnsureSwapfileCalls++;
        return Task.CompletedTask;
    }

    public Task DropPageCachesAsync(CancellationToken ct)
    {
        DropPageCachesCalls++;
        return Task.CompletedTask;
    }

    public Task RebootHostAsync(CancellationToken ct)
    {
        OnRebootHost?.Invoke();
        RebootHostCalls++;
        return Task.CompletedTask;
    }

    public int TotalMutatingCalls =>
        PruneStoppedContainersCalls + PruneDanglingImagesCalls + EnsureSwapfileCalls + DropPageCachesCalls + RebootHostCalls;
}
