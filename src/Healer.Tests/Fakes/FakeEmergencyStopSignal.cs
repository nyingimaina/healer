using Healer.Core.Abstractions;

namespace Healer.Tests.Fakes;

public sealed class FakeEmergencyStopSignal : IEmergencyStopSignal
{
    public string? DisabledReason { get; set; }

    public List<string> DisableCalls { get; } = [];

    public Task<string?> GetDisabledReasonAsync(CancellationToken ct) => Task.FromResult(DisabledReason);

    public Task DisableAsync(string reason, CancellationToken ct)
    {
        DisableCalls.Add(reason);
        DisabledReason = reason;
        return Task.CompletedTask;
    }
}
