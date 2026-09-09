using System.Text.RegularExpressions;

namespace Healer.Setup.Logic;

/// <summary>
/// Validates the two unavoidable free-text fields in the wizard, with a plain-language reason for
/// a rejection so the on-screen hint can say exactly what's wrong rather than just "invalid".
/// Pure/static and I/O-free — the only network call (the actual test-send) lives in
/// Healer.Host.Notification.TelegramNotifier.TestAsync, which the wizard calls separately.
/// </summary>
public static partial class TelegramFieldValidator
{
    [GeneratedRegex(@"^\d+:[A-Za-z0-9_-]+$")]
    private static partial Regex BotTokenPattern();

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex ChatIdPattern();

    public static (bool IsValid, string? Reason) ValidateBotToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (false, "Paste the token BotFather gave you.");
        }

        return BotTokenPattern().IsMatch(value.Trim())
            ? (true, null)
            : (false, "Should look like 123456789:ABC-defGhIJKlmNoPQRsTUVwxyZ — a number, a colon, then a mix of letters/digits/-/_.");
    }

    public static (bool IsValid, string? Reason) ValidateChatId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (false, "Enter the chat id the bot should message.");
        }

        return ChatIdPattern().IsMatch(value.Trim())
            ? (true, null)
            : (false, "Should be a number, e.g. 123456789 for a personal chat, or a negative number like -1001234567890 for a group.");
    }
}
