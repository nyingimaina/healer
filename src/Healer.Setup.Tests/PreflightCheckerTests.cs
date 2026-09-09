using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class PreflightCheckerTests
{
    [Fact]
    public void DockerSocketCheck_Passes_WhenSocketPathExists()
    {
        var results = PreflightChecker.RunAll(
            dockerSocketPath: "/var/run/docker.sock",
            pathExists: path => path == "/var/run/docker.sock",
            commandExists: _ => true);

        var docker = Assert.Single(results, r => r.Name == "Docker is running");
        Assert.True(docker.Passed);
    }

    [Fact]
    public void DockerSocketCheck_Fails_WithGuidance_WhenSocketMissing()
    {
        var results = PreflightChecker.RunAll(
            dockerSocketPath: "/var/run/docker.sock",
            pathExists: _ => false,
            commandExists: _ => true);

        var docker = Assert.Single(results, r => r.Name == "Docker is running");
        Assert.False(docker.Passed);
        Assert.Contains("docker.sock", docker.DetailIfFailed);
    }

    [Fact]
    public void SystemdCheck_Fails_WhenSystemctlNotOnPath()
    {
        var results = PreflightChecker.RunAll(
            pathExists: _ => true,
            commandExists: cmd => cmd != "systemctl");

        var systemd = Assert.Single(results, r => r.Name == "systemd is available");
        Assert.False(systemd.Passed);
    }

    [Fact]
    public void RunAll_AlwaysIncludesArchitectureAndDiskChecks()
    {
        var results = PreflightChecker.RunAll(pathExists: _ => true, commandExists: _ => true);

        Assert.Contains(results, r => r.Name == "Supported CPU architecture");
        Assert.Contains(results, r => r.Name == "Enough free disk space");
    }
}
