using Healer.Setup.Logic;

namespace Healer.Setup.Tests;

public class ComposeProjectDirectoryValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsBlankDirectory(string directory)
    {
        var (valid, reason) = ComposeProjectDirectoryValidator.Validate(directory);

        Assert.False(valid);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Validate_RejectsDirectoryThatDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "healer-setup-tests-missing-" + Guid.NewGuid());

        var (valid, reason) = ComposeProjectDirectoryValidator.Validate(missing);

        Assert.False(valid);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Validate_RejectsDirectoryWithNoComposeFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "healer-setup-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var (valid, reason) = ComposeProjectDirectoryValidator.Validate(dir);

            Assert.False(valid);
            Assert.NotNull(reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("docker-compose.yaml")]
    [InlineData("compose.yml")]
    [InlineData("compose.yaml")]
    public void Validate_AcceptsDirectoryContainingAnyRecognizedComposeFileName(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "healer-setup-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, fileName), "services: {}");

            var (valid, reason) = ComposeProjectDirectoryValidator.Validate(dir);

            Assert.True(valid);
            Assert.Null(reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("/opt/milele", "milele")]
    [InlineData("/opt/milele/", "milele")]
    [InlineData("", "")]
    public void DeriveProjectName_ReturnsTheFinalPathSegment(string directory, string expected)
    {
        Assert.Equal(expected, ComposeProjectDirectoryValidator.DeriveProjectName(directory));
    }
}
