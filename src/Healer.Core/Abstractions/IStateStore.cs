using Healer.Core.Models;

namespace Healer.Core.Abstractions;

public interface IStateStore
{
    Task<HealerState> LoadAsync(CancellationToken ct);

    Task SaveAsync(HealerState state, CancellationToken ct);
}
