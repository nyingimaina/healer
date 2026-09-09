using Healer.Core.Abstractions;

namespace Healer.Tests.Fakes;

public sealed class FakeNotifier : INotifier
{
    public List<string> SentMessages { get; } = [];

    public Task SendAsync(string message, CancellationToken ct)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }
}
