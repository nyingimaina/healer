using System.Text.Json;
using Healer.Core.Abstractions;
using Healer.Host.Serialization;
using Serilog;

namespace Healer.Host.Notification;

/// <summary>
/// Talks directly to the Telegram Bot HTTP API. Deliberately independent of the user's Windows-only
/// `SemaNami` CLI (which cannot run on these Linux targets) — same TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID
/// naming convention, entirely separate implementation.
/// </summary>
public sealed class TelegramNotifier(HttpClient httpClient, string botToken, string chatId) : INotifier
{
    public async Task SendAsync(string message, CancellationToken ct)
    {
        var request = new TelegramSendMessageRequest { ChatId = chatId, Text = message };
        var json = JsonSerializer.Serialize(request, HealerJsonContext.Default.TelegramSendMessageRequest);

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync($"https://api.telegram.org/bot{botToken}/sendMessage", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Error("Telegram sendMessage failed with {StatusCode}: {Body}", (int)response.StatusCode, body);
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Used by the setup wizard's "Send Test Message" button and Healer.Host's startup self-check.</summary>
    public static async Task<(bool Success, string? Error)> TestAsync(HttpClient httpClient, string botToken, string chatId, CancellationToken ct)
    {
        try
        {
            var notifier = new TelegramNotifier(httpClient, botToken, chatId);
            await notifier.SendAsync("✅ Healer connected successfully. You'll get alerts here when something needs attention.", ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
