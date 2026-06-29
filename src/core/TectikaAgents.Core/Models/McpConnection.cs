using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public enum McpConnectionStatus { Connected, Error, Disconnected }

/// <summary>A per-board connection to one catalog MCP integration. The token itself lives in Key Vault
/// under <see cref="SecretName"/>; this record only references it (mirrors GitHubRepoConnection.PatSecretName).</summary>
public sealed class McpConnection
{
    [JsonPropertyName("connectionId")] public string ConnectionId { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("catalogId")]    public string CatalogId { get; set; } = string.Empty;
    [JsonPropertyName("displayName")]  public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("secretName")]   public string SecretName { get; set; } = string.Empty;
    [JsonPropertyName("status")]       public McpConnectionStatus Status { get; set; } = McpConnectionStatus.Connected;
    [JsonPropertyName("lastValidatedAt")] public DateTimeOffset? LastValidatedAt { get; set; }
    [JsonPropertyName("createdBy")]    public string? CreatedBy { get; set; }
    [JsonPropertyName("createdAt")]    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
