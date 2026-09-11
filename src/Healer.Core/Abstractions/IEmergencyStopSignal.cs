namespace Healer.Core.Abstractions;

/// <summary>
/// Reads/writes the single sentinel that makes Healer stop taking any automatic action — created
/// either by a human (the `healer-disable` command, or Ctrl+D in `healer-status`) or by Healer
/// itself (<see cref="Decision.EmergencyActionRateBreaker"/>, when it detects it's taken far more
/// mutating actions than should ever be possible under normal cooldown-gated operation). Whichever
/// origin, <see cref="Engine.HealingEngine"/> treats its presence identically to `DryRun = true` —
/// one mechanism, not two.
/// </summary>
public interface IEmergencyStopSignal
{
    /// <returns>The reason Healer is disabled, or null if it isn't.</returns>
    Task<string?> GetDisabledReasonAsync(CancellationToken ct);

    Task DisableAsync(string reason, CancellationToken ct);
}
