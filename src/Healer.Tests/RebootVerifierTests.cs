using Healer.Core.Decision;

namespace Healer.Tests;

public class RebootVerifierTests
{
    private static readonly DateTimeOffset Requested = new(2026, 1, 1, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_ReturnsNothingPending_WhenNoRebootWasRequested()
    {
        var result = RebootVerifier.Evaluate(pendingRequestedUtc: null, hostBootTimeUtc: Requested.AddMinutes(-5), nowUtc: Requested);

        Assert.Equal(RebootVerificationOutcome.NothingPending, result);
    }

    [Fact]
    public void Evaluate_ReturnsVerified_WhenBootTimeIsAfterTheRequest()
    {
        var bootTime = Requested.AddMinutes(2); // host rebooted 2 minutes after the request
        var now = Requested.AddMinutes(3);

        var result = RebootVerifier.Evaluate(Requested, bootTime, now);

        Assert.Equal(RebootVerificationOutcome.Verified, result);
    }

    [Fact]
    public void Evaluate_ReturnsStillWaiting_WhenBootTimeHasNotAdvancedAndGraceWindowNotElapsed()
    {
        var staleBootTime = Requested.AddMinutes(-10); // host hasn't rebooted since before the request
        var now = Requested.AddMinutes(5); // well within the default 30-minute window

        var result = RebootVerifier.Evaluate(Requested, staleBootTime, now);

        Assert.Equal(RebootVerificationOutcome.StillWaiting, result);
    }

    [Fact]
    public void Evaluate_ReturnsTimedOut_WhenGraceWindowElapsedWithoutBootTimeAdvancing()
    {
        var staleBootTime = Requested.AddMinutes(-10);
        var now = Requested.Add(RebootVerifier.DefaultGraceWindow).AddMinutes(1); // just past the window

        var result = RebootVerifier.Evaluate(Requested, staleBootTime, now);

        Assert.Equal(RebootVerificationOutcome.TimedOut, result);
    }

    [Fact]
    public void Evaluate_StillWaiting_ExactlyAtTheGraceWindowBoundary()
    {
        var staleBootTime = Requested.AddMinutes(-10);
        var now = Requested.Add(RebootVerifier.DefaultGraceWindow); // exactly at the boundary, not past it

        var result = RebootVerifier.Evaluate(Requested, staleBootTime, now);

        Assert.Equal(RebootVerificationOutcome.StillWaiting, result);
    }

    [Fact]
    public void Evaluate_HonorsACustomGraceWindow()
    {
        var staleBootTime = Requested.AddMinutes(-10);
        var now = Requested.AddMinutes(6);

        var result = RebootVerifier.Evaluate(Requested, staleBootTime, now, graceWindow: TimeSpan.FromMinutes(5));

        Assert.Equal(RebootVerificationOutcome.TimedOut, result);
    }
}
