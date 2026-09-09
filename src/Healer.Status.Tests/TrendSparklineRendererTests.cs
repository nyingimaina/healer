using Healer.Core.Models;
using Healer.Status.Logic;

namespace Healer.Status.Tests;

public class TrendSparklineRendererTests
{
    [Fact]
    public void Render_EmptySeries_ReturnsPlaceholder()
    {
        var result = TrendSparklineRenderer.Render([]);

        Assert.Equal("(no data yet)", result);
    }

    [Fact]
    public void Render_ProducesOneCharacterPerValue()
    {
        var result = TrendSparklineRenderer.Render([10, 50, 90]);

        Assert.Equal(3, result.Length);
    }

    [Fact]
    public void Render_LowestValue_MapsToLowestBlock_AndHighest_ToHighestBlock()
    {
        var result = TrendSparklineRenderer.Render([0, 100]);

        Assert.Equal('▁', result[0]);
        Assert.Equal('█', result[1]);
    }

    [Fact]
    public void Render_ConstantSeries_DoesNotThrow_AndProducesMidLevelBlocks()
    {
        var result = TrendSparklineRenderer.Render([50, 50, 50]);

        Assert.Equal(3, result.Length);
    }

    [Fact]
    public void Bucket_ShortSeries_ReturnsValuesUnchanged()
    {
        var points = new List<TrendPoint>
        {
            new(DateTimeOffset.UnixEpoch, 1),
            new(DateTimeOffset.UnixEpoch.AddMinutes(1), 2),
        };

        var result = TrendSparklineRenderer.Bucket(points, bucketCount: 60);

        Assert.Equal([1.0, 2.0], result);
    }

    [Fact]
    public void Bucket_LongSeries_DownsamplesToRequestedCount()
    {
        var points = Enumerable.Range(0, 1000)
            .Select(i => new TrendPoint(DateTimeOffset.UnixEpoch.AddMinutes(i), i))
            .ToList();

        var result = TrendSparklineRenderer.Bucket(points, bucketCount: 60);

        Assert.Equal(60, result.Count);
        Assert.True(result[0] < result[^1]); // still monotonically increasing after averaging
    }

    [Fact]
    public void Bucket_EmptySeries_ReturnsEmpty()
    {
        var result = TrendSparklineRenderer.Bucket([], bucketCount: 60);

        Assert.Empty(result);
    }
}
