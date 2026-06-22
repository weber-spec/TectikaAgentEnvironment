using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public enum PreviewStatus { Provisioning, Running, Failed, Stopped }

public class PreviewSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty; // == DNS label == ACI group name

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty; // partition key

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PreviewStatus Status { get; set; } = PreviewStatus.Provisioning;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastActivityAt")]
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;
}
