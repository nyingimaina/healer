namespace Healer.Core.Abstractions;

public sealed record ComposeProjectRef(string Name, string WorkingDirectory);

/// <summary>
/// Restarts an entire docker-compose project via the `docker compose` CLI so compose itself handles
/// dependency-aware shutdown/startup ordering, rather than Healer restarting each container
/// individually through the Docker API (as it does for crash-loop/worst-offender restarts).
/// Implemented in Healer.Host by shelling out to the `docker` binary.
/// </summary>
public interface IComposeRestartExecutor
{
    /// <summary>
    /// Restarts every service in the project except those named in <paramref name="excludedServiceNames"/>.
    /// When nothing is excluded, this is a plain `docker compose restart` with no service arguments —
    /// exclusions require first listing the project's actual services and naming every other one
    /// explicitly, since `docker compose restart` has no "all except" syntax of its own.
    /// </summary>
    Task RestartProjectAsync(ComposeProjectRef project, IReadOnlyList<string> excludedServiceNames, CancellationToken ct);
}
