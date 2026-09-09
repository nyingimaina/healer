namespace Healer.Setup.Logic;

/// <summary>Validates the required server name — the only other unavoidable typed field besides the Telegram credentials, though it's pre-filled with the box's hostname so most people never need to type anything here at all.</summary>
public static class ServerNameValidator
{
    public const int MaxLength = 63;

    public static (bool IsValid, string? Reason) Validate(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return (false, "Give this server a name — it's shown on every alert so you can tell which box sent it.");
        }

        return trimmed.Length <= MaxLength
            ? (true, null)
            : (false, $"Keep it to {MaxLength} characters or fewer.");
    }
}
