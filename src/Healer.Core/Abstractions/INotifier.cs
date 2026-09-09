namespace Healer.Core.Abstractions;

/// <summary>Transport-only. Message wording lives entirely in Decision.MessageFormatter so it's testable without a real Telegram call.</summary>
public interface INotifier
{
    Task SendAsync(string message, CancellationToken ct);
}
