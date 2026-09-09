using Healer.Core.Models;

namespace Healer.Core.Abstractions;

/// <summary>Read/act on containers via the Docker Engine API. Implemented in Healer.Host over the Unix socket.</summary>
public interface IContainerRuntime
{
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct);

    Task RestartContainerAsync(string containerName, TimeSpan timeout, CancellationToken ct);
}
