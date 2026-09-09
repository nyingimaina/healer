using Healer.Core.Abstractions;
using Healer.Core.Models;

namespace Healer.Tests.Fakes;

public sealed class FakeStateStore : IStateStore
{
    public HealerState State { get; set; } = new();

    public int SaveCalls { get; private set; }

    public Task<HealerState> LoadAsync(CancellationToken ct) => Task.FromResult(State);

    public Task SaveAsync(HealerState state, CancellationToken ct)
    {
        State = state;
        SaveCalls++;
        return Task.CompletedTask;
    }
}
