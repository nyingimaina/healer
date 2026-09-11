using Healer.Core.Decision;

namespace Healer.Tests;

public class ScheduledSuccessNotificationGateTests
{
    [Fact]
    public void Evaluate_FirstEverSuccess_AlwaysNotifies()
    {
        var (shouldNotify, successes, threshold) = ScheduledSuccessNotificationGate.Evaluate(successesSinceLastNotify: 0, skipThreshold: 0, maxSkipThreshold: 16);

        Assert.True(shouldNotify);
        Assert.Equal(0, successes);
        Assert.Equal(1, threshold);
    }

    [Fact]
    public void Evaluate_ProducesTheExpectedDoublingSequence_UpToTheCap()
    {
        // Simulates 60 consecutive successful nightly runs and records which occurrence numbers
        // (1-indexed) actually notify — this is the exact table from the design discussion.
        var successes = 0;
        var threshold = 0;
        var notifiedAt = new List<int>();

        for (var occurrence = 1; occurrence <= 60; occurrence++)
        {
            var (shouldNotify, newSuccesses, newThreshold) = ScheduledSuccessNotificationGate.Evaluate(successes, threshold, maxSkipThreshold: 16);
            successes = newSuccesses;
            threshold = newThreshold;
            if (shouldNotify)
            {
                notifiedAt.Add(occurrence);
            }
        }

        Assert.Equal([1, 3, 6, 11, 20, 37, 54], notifiedAt);
    }

    [Fact]
    public void Evaluate_CapsTheSkipThreshold_AndNeverGrowsPastIt()
    {
        var successes = 0;
        var threshold = 0;

        for (var i = 0; i < 200; i++)
        {
            (_, successes, threshold) = ScheduledSuccessNotificationGate.Evaluate(successes, threshold, maxSkipThreshold: 16);
        }

        Assert.Equal(16, threshold);
    }

    [Fact]
    public void Evaluate_HonorsACustomMaxSkipThreshold()
    {
        var successes = 0;
        var threshold = 0;

        for (var i = 0; i < 50; i++)
        {
            (_, successes, threshold) = ScheduledSuccessNotificationGate.Evaluate(successes, threshold, maxSkipThreshold: 4);
        }

        Assert.Equal(4, threshold);
    }

    [Fact]
    public void Evaluate_ExactlyAtThreshold_DoesNotYetNotify()
    {
        // After the first notify, threshold = 1. A single subsequent success (successesSoFar = 1)
        // is NOT strictly greater than threshold 1, so it must not notify yet.
        var (shouldNotify, successes, threshold) = ScheduledSuccessNotificationGate.Evaluate(successesSinceLastNotify: 0, skipThreshold: 1, maxSkipThreshold: 16);

        Assert.False(shouldNotify);
        Assert.Equal(1, successes);
        Assert.Equal(1, threshold);
    }
}
