using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>Tenant-level global tool policy: explicit enable/disable overrides per tool id. A tool absent from
/// <see cref="Overrides"/> uses its source default (Foundry built-in → off/opt-in; board + integration → on).
/// One document per tenant (Id = tenantId). Core Explore/Control board tools are never overridable (enforced
/// by the controller), so they never appear here.</summary>
public sealed class ToolPolicy
{
    [JsonPropertyName("id")]        public string Id { get; set; } = string.Empty;      // == tenantId
    [JsonPropertyName("tenantId")]  public string TenantId { get; set; } = string.Empty;

    /// <summary>toolId → enabled. toolId scheme: "board:{name}", "foundry:{id}", "integration:{catalogId}:{tool}".</summary>
    [JsonPropertyName("overrides")] public Dictionary<string, bool> Overrides { get; set; } = new();

    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
