using Healer.Core.Abstractions;
using Healer.Core.Models;

namespace Healer.Tests.Fakes;

public sealed class FakeContainerRuntime : IContainerRuntime
{
    public List<ContainerInfo> Containers { get; } = [];

    public List<string> RestartCalls { get; } = [];

    /// <summary>Optional hook so a test can simulate the effect of a restart (e.g. clear unhealthy status) before the next tick reads containers.</summary>
    public Action<string>? OnRestart { get; set; }

    /// <summary>When set, RestartContainerAsync throws this instead of succeeding — used to verify the engine catches and reports restart failures.</summary>
    public Exception? ThrowOnRestart { get; set; }

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ContainerInfo>>(Containers.ToList());

    public Task RestartContainerAsync(string containerName, TimeSpan timeout, CancellationToken ct)
    {
        RestartCalls.Add(containerName);
        if (ThrowOnRestart is not null)
        {
            throw ThrowOnRestart;
        }

        OnRestart?.Invoke(containerName);
        return Task.CompletedTask;
    }
}
