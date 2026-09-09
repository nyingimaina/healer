using System.Text.Json.Serialization;
using Healer.Core.Configuration;
using Healer.Core.Models;
using Healer.Host.Docker;
using Healer.Host.Notification;

namespace Healer.Host.Serialization;

/// <summary>
/// Source-generated (de)serialization for every JSON shape Healer.Host touches — config, persisted
/// state, Docker Engine API responses, and the Telegram request body. Required for Native AOT/trim
/// safety: reflection-based JsonSerializer.Serialize/Deserialize would either fail or need explicit
/// [DynamicallyAccessedMembers] annotations under trimming; source generation needs neither.
/// The generator resolves each root type's full reachable graph (nested records, List&lt;T&gt;,
/// Dictionary&lt;K,V&gt;, etc.) automatically — only root types need to be listed here.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HealerConfig))]
[JsonSerializable(typeof(HealerState))]
[JsonSerializable(typeof(List<DockerContainerListItem>))]
[JsonSerializable(typeof(DockerContainerInspect))]
[JsonSerializable(typeof(DockerStatsResponse))]
[JsonSerializable(typeof(TelegramSendMessageRequest))]
public partial class HealerJsonContext : JsonSerializerContext;
