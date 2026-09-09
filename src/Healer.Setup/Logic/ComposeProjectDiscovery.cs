using Healer.Host.Docker;

namespace Healer.Setup.Logic;

public sealed record DiscoveredComposeProject(string Name, string WorkingDirectory);

/// <summary>
/// Finds running docker-compose projects Healer can offer as a check-off list, with no typing
/// required — the governing rule for this whole wizard. `docker compose up` stamps every container
/// it starts with `com.docker.compose.project` and `com.docker.compose.project.working_dir` labels
/// (the same mechanism `docker compose ls` itself reads); grouping running containers by that first
/// label and taking the second gives every distinct project's directory without ever asking the
/// operator where their compose file lives. Only running (not `all`) containers are considered,
/// since a project with nothing currently running has no live label data to discover from.
/// </summary>
public static class ComposeProjectDiscovery
{
    private const string ProjectLabel = "com.docker.compose.project";
    private const string WorkingDirLabel = "com.docker.compose.project.working_dir";

    public static IReadOnlyList<DiscoveredComposeProject> GroupByProject(IReadOnlyList<DockerContainerListItem> containers) =>
        containers
            .Where(c => c.Labels.ContainsKey(ProjectLabel) && c.Labels.ContainsKey(WorkingDirLabel))
            .GroupBy(c => c.Labels[ProjectLabel])
            .Select(g => new DiscoveredComposeProject(g.Key, g.First().Labels[WorkingDirLabel]))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Talks to the real Docker socket. Never throws — a wizard step can't do much with a discovery
    /// failure beyond falling back to manual entry, and Healer's preflight check already surfaces a
    /// genuinely broken/missing Docker installation elsewhere in the wizard.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoveredComposeProject>> DiscoverRunningProjectsAsync(string dockerSocketPath, CancellationToken ct)
    {
        try
        {
            using var client = new DockerApiClient(dockerSocketPath);
            var containers = await client.ListContainersAsync(all: false, ct);
            return GroupByProject(containers);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
