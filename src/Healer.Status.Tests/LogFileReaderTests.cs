using Healer.Status.Logic;

namespace Healer.Status.Tests;

public class LogFileReaderTests
{
    [Fact]
    public void FindLatestLogFile_ReturnsNull_WhenDirectoryDoesNotExist()
    {
        var result = LogFileReader.FindLatestLogFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.Null(result);
    }

    [Fact]
    public void FindLatestLogFile_ReturnsMostRecentlyModifiedLogFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "healer-status-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var older = Path.Combine(dir, "healer-20260101.log");
            var newer = Path.Combine(dir, "healer-20260102.log");
            File.WriteAllText(older, "old");
            File.WriteAllText(newer, "new");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            var result = LogFileReader.FindLatestLogFile(dir);

            Assert.Equal(newer, result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TailLines_ReturnsAllLines_WhenFewerThanMax()
    {
        var result = LogFileReader.TailLines(["a", "b"], maxLines: 10);

        Assert.Equal(["a", "b"], result);
    }

    [Fact]
    public void TailLines_ReturnsOnlyTheLastMaxLines()
    {
        var lines = Enumerable.Range(1, 100).Select(i => i.ToString()).ToList();

        var result = LogFileReader.TailLines(lines, maxLines: 5);

        Assert.Equal(["96", "97", "98", "99", "100"], result);
    }

    [Fact]
    public void BuildExportFilePath_PlacesATimestampedFileInsideTheLogDirectory()
    {
        var now = new DateTimeOffset(2026, 3, 5, 14, 30, 45, TimeSpan.Zero);

        var path = LogFileReader.BuildExportFilePath("/var/log/healer", now);

        Assert.Equal(Path.Combine("/var/log/healer", "healer-status-export-20260305-143045.txt"), path);
    }

    [Fact]
    public void BuildExportFilePath_DifferentTimestamps_ProduceDifferentPaths()
    {
        var first = LogFileReader.BuildExportFilePath("/var/log/healer", new DateTimeOffset(2026, 3, 5, 14, 30, 45, TimeSpan.Zero));
        var second = LogFileReader.BuildExportFilePath("/var/log/healer", new DateTimeOffset(2026, 3, 5, 14, 30, 46, TimeSpan.Zero));

        Assert.NotEqual(first, second);
    }
}
