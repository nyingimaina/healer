namespace Healer.Setup.Logic;

/// <summary>
/// A minimal, independent Telegram test-send used only by the wizard's "Send Test Message" button.
/// Deliberately not shared with Healer.Host.Notification.TelegramNotifier (Healer.Setup references
/// only Healer.Core + Terminal.Gui, matching the architecture's Core/Host/Setup/Status split) — this
/// is a few lines, duplicating it here is cheaper than coupling the wizard to the daemon project.
/// </summary>
public static class TelegramTestSender
{
    public static async Task<(bool Success, string? Error)> SendTestMessageAsync(string botToken, string chatId, string serverName, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
            var prefix = string.IsNullOrWhiteSpace(serverName) ? "" : $"[{serverName}] ";
            var body = new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = $"✅ {prefix}Healer connected successfully. You'll get alerts here when something needs attention.",
            };

            using var response = await http.PostAsync(url, new FormUrlEncodedContent(body), ct);
            if (!response.IsSuccessStatusCode)
            {
                var body2 = await response.Content.ReadAsStringAsync(ct);
                return (false, $"Telegram returned {(int)response.StatusCode}: {body2}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
