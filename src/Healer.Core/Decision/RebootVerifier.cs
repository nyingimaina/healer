namespace Healer.Core.Decision;

public enum RebootVerificationOutcome
{
    /// <summary>No reboot has been requested — nothing to check.</summary>
    NothingPending,

    /// <summary>A reboot was requested, but the host's boot time hasn't advanced past the request
    /// yet and the grace window hasn't elapsed either — say nothing yet, don't spam every tick.</summary>
    StillWaiting,

    /// <summary>The host's boot time is after the request — a genuine reboot happened and Healer is
    /// back up.</summary>
    Verified,

    /// <summary>The grace window elapsed and boot time never advanced — Healer restarted (this tick
    /// is running, after all) but the host itself apparently never rebooted, or this is a later,
    /// unrelated daemon start that found a stale marker.</summary>
    TimedOut,
}

/// <summary>
/// Resolves a pending host-reboot request (<see cref="Models.HealerState.PendingRebootRequestedUtc"/>)
/// against the host's actual boot time — the only reliable way to know a genuine reboot happened, as
/// opposed to just the `healer` service restarting. Exists because a reboot's own process never
/// survives to report success itself (see docs/ARCHITECTURE.md): whatever requests a reboot — the
/// scheduled-reboot action, or the setup wizard's "test reboot now" button — sets the marker and
/// then reboots blind; this is checked every tick after the fact, once the daemon is running again.
/// </summary>
public static class RebootVerifier
{
    public static readonly TimeSpan DefaultGraceWindow = TimeSpan.FromMinutes(30);

    public static RebootVerificationOutcome Evaluate(
        DateTimeOffset? pendingRequestedUtc, DateTimeOffset hostBootTimeUtc, DateTimeOffset nowUtc, TimeSpan? graceWindow = null)
    {
        if (pendingRequestedUtc is not { } requestedUtc)
        {
            return RebootVerificationOutcome.NothingPending;
        }

        if (hostBootTimeUtc > requestedUtc)
        {
            return RebootVerificationOutcome.Verified;
        }

        var window = graceWindow ?? DefaultGraceWindow;
        return nowUtc - requestedUtc > window
            ? RebootVerificationOutcome.TimedOut
            : RebootVerificationOutcome.StillWaiting;
    }
}
