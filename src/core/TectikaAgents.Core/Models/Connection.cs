using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public enum ConnectionStatus { Connected, Error, Disconnected }

/// <summary>Whether a connection is shared across the organization or private to its creator. Persisted and
/// shown in the UI, but NOT enforced yet — ownership-based access control lands with Microsoft (Entra) auth.</summary>
public enum ConnectionScope { Organization, Private }

/// <summary>A tenant-level (organization) registry entry for one external connection — the single source of
/// truth boards and agents reference by <see cref="Id"/>. The credential itself lives in Key Vault under
/// <see cref="SecretName"/> (this record only references it, mirroring <see cref="GitHubRepoConnection.PatSecretName"/>).
/// Multiple connections may point at the same catalog resource
/// (e.g. two Gmail accounts), distinguished by <see cref="DisplayName"/>.</summary>
public sealed class Connection
{
    [JsonPropertyName("id")]              public string Id { get; set; } = $"conn_{Guid.NewGuid():N}";
    [JsonPropertyName("tenantId")]        public string TenantId { get; set; } = string.Empty;

    /// <summary>Catalog entry id this connection instantiates (e.g. "slack", "anthropic", "github").</summary>
    [JsonPropertyName("catalogId")]       public string CatalogId { get; set; } = string.Empty;

    /// <summary>Denormalized catalog category ("model" | "agent-tool" | "source-control") for grouping/filtering
    /// without re-reading the catalog.</summary>
    [JsonPropertyName("category")]        public string Category { get; set; } = string.Empty;

    /// <summary>User-given name, e.g. "Gmail - Marketing". Lets several connections share one catalog resource.</summary>
    [JsonPropertyName("displayName")]     public string DisplayName { get; set; } = string.Empty;

    /// <summary>Key Vault secret NAME holding the credential (never the value). For multi-field auth the value
    /// is a JSON object keyed by <see cref="AuthField.Name"/>; for single-field auth it is the raw token.</summary>
    [JsonPropertyName("secretName")]      public string SecretName { get; set; } = string.Empty;

    [JsonPropertyName("status")]          public ConnectionStatus Status { get; set; } = ConnectionStatus.Connected;
    [JsonPropertyName("lastValidatedAt")] public DateTimeOffset? LastValidatedAt { get; set; }
    [JsonPropertyName("createdBy")]       public string? CreatedBy { get; set; }
    [JsonPropertyName("createdAt")]       public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Non-secret configuration (e.g. { "defaultFrom": "…" } for email, { "owner": "…" } for GitHub).</summary>
    [JsonPropertyName("metadata")]        public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>True for platform-provided pseudo-connections that arrive pre-connected (e.g. Foundry). Never
    /// persisted — injected at read time — and carries no secret.</summary>
    [JsonPropertyName("isSystem")]        public bool IsSystem { get; set; }

    /// <summary>Private vs organization. Stored + displayed; access control deferred to Entra auth.</summary>
    [JsonPropertyName("scope")]           public ConnectionScope Scope { get; set; } = ConnectionScope.Organization;
}
