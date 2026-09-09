using Healer.Core.Abstractions;

namespace Healer.Tests.Fakes;

public sealed class FakeComposeRestartExecutor : IComposeRestartExecutor
{
    public List<(ComposeProjectRef Project, IReadOnlyList<string> ExcludedServiceNames)> RestartCalls { get; } = [];

    public Exception? ThrowOnRestart { get; set; }

    public Task RestartProjectAsync(ComposeProjectRef project, IReadOnlyList<string> excludedServiceNames, CancellationToken ct)
    {
        if (ThrowOnRestart is { } ex)
        {
            throw ex;
        }

        RestartCalls.Add((project, excludedServiceNames));
        return Task.CompletedTask;
    }
}
