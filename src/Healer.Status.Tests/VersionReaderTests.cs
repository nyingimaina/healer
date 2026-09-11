using Healer.Status.Logic;

namespace Healer.Status.Tests;

public class VersionReaderTests
{
    [Fact]
    public void Read_ReturnsTheTrimmedFileContent()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        File.WriteAllText(path, "v1.0.10\n");
        try
        {
            Assert.Equal("v1.0.10", VersionReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ReturnsUnknownWhenTheFileDoesNotExist()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        Assert.Equal("unknown", VersionReader.Read(path));
    }
}
