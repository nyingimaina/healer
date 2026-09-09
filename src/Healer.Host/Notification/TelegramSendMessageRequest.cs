using System.Text.Json.Serialization;

namespace Healer.Host.Notification;

public sealed class TelegramSendMessageRequest
{
    [JsonPropertyName("chat_id")]
    public string ChatId { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}
