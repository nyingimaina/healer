using Healer.Host.Docker;

namespace Healer.Host.Tests;

public class DockerComposeRestartExecutorTests
{
    [Fact]
    public void ResolveComposeCommand_PrefersThePluginWhenAvailable()
    {
        var (executable, baseArgs) = DockerComposeRestartExecutor.ResolveComposeCommand(pluginAvailable: true, standaloneAvailable: true);

        Assert.Equal("docker", executable);
        Assert.Equal(["compose"], baseArgs);
    }

    [Fact]
    public void ResolveComposeCommand_FallsBackToStandalone_WhenOnlyThatIsAvailable()
    {
        var (executable, baseArgs) = DockerComposeRestartExecutor.ResolveComposeCommand(pluginAvailable: false, standaloneAvailable: true);

        Assert.Equal("docker-compose", executable);
        Assert.Empty(baseArgs);
    }

    [Fact]
    public void ResolveComposeCommand_ThrowsNamingBoth_WhenNeitherIsAvailable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DockerComposeRestartExecutor.ResolveComposeCommand(pluginAvailable: false, standaloneAvailable: false));

        Assert.Contains("docker compose", ex.Message);
        Assert.Contains("docker-compose", ex.Message);
    }
}
