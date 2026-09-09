using Healer.Host.Docker;
using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class ComposeProjectDiscoveryTests
{
    private static DockerContainerListItem Container(string id, Dictionary<string, string>? labels = null) => new()
    {
        Id = id,
        Names = [$"/{id}"],
        Labels = labels ?? [],
    };

    [Fact]
    public void GroupByProject_ReturnsEmpty_WhenNoContainersHaveComposeLabels()
    {
        var containers = new List<DockerContainerListItem> { Container("web") };

        var result = ComposeProjectDiscovery.GroupByProject(containers);

        Assert.Empty(result);
    }

    [Fact]
    public void GroupByProject_IgnoresContainersMissingEitherComposeLabel()
    {
        var containers = new List<DockerContainerListItem>
        {
            Container("web", new() { ["com.docker.compose.project"] = "milele" }), // missing working_dir
            Container("db", new() { ["com.docker.compose.project.working_dir"] = "/opt/milele" }), // missing project name
        };

        var result = ComposeProjectDiscovery.GroupByProject(containers);

        Assert.Empty(result);
    }

    [Fact]
    public void GroupByProject_GroupsMultipleContainersOfTheSameProjectIntoOneEntry()
    {
        var containers = new List<DockerContainerListItem>
        {
            Container("web", new() { ["com.docker.compose.project"] = "milele", ["com.docker.compose.project.working_dir"] = "/opt/milele" }),
            Container("db", new() { ["com.docker.compose.project"] = "milele", ["com.docker.compose.project.working_dir"] = "/opt/milele" }),
        };

        var result = ComposeProjectDiscovery.GroupByProject(containers);

        var project = Assert.Single(result);
        Assert.Equal("milele", project.Name);
        Assert.Equal("/opt/milele", project.WorkingDirectory);
    }

    [Fact]
    public void GroupByProject_ReturnsDistinctProjectsSortedByName()
    {
        var containers = new List<DockerContainerListItem>
        {
            Container("web1", new() { ["com.docker.compose.project"] = "skips", ["com.docker.compose.project.working_dir"] = "/opt/skips" }),
            Container("web2", new() { ["com.docker.compose.project"] = "milele", ["com.docker.compose.project.working_dir"] = "/opt/milele" }),
        };

        var result = ComposeProjectDiscovery.GroupByProject(containers);

        Assert.Equal(["milele", "skips"], result.Select(p => p.Name));
    }
}
