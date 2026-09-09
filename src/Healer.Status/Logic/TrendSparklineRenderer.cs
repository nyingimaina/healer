using Healer.Core.Models;

namespace Healer.Status.Logic;

/// <summary>
/// Renders a trend series as a compact Unicode-block sparkline — "is this creeping up" at a glance,
/// with no charting library and no axes/legend, since a terminal can't credibly render those anyway.
/// Pure and testable independent of the TUI or the database.
/// </summary>
public static class TrendSparklineRenderer
{
    private static readonly char[] Blocks = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    public static string Render(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return "(no data yet)";
        }

        var min = values.Min();
        var max = values.Max();
        var range = max - min;

        var chars = values.Select(v =>
        {
            var normalized = range <= 0 ? 0.5 : (v - min) / range;
            var index = (int)Math.Round(normalized * (Blocks.Length - 1));
            return Blocks[Math.Clamp(index, 0, Blocks.Length - 1)];
        });

        return new string(chars.ToArray());
    }

    /// <summary>Downsamples a raw trend series to at most `bucketCount` points by averaging, so a long time range still renders as one compact line.</summary>
    public static IReadOnlyList<double> Bucket(IReadOnlyList<TrendPoint> points, int bucketCount)
    {
        if (points.Count == 0)
        {
            return [];
        }

        if (points.Count <= bucketCount || bucketCount <= 0)
        {
            return points.Select(p => p.Value).ToList();
        }

        var bucketSize = (double)points.Count / bucketCount;
        var result = new List<double>(bucketCount);

        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = (int)(bucket * bucketSize);
            var end = (int)Math.Min(points.Count, (bucket + 1) * bucketSize);
            if (end <= start)
            {
                end = start + 1;
            }

            result.Add(points.Skip(start).Take(end - start).Average(p => p.Value));
        }

        return result;
    }
}
