using System.Text.Json.Serialization;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Usage;

/// <summary>Immutable ledger record for one billed LLM unit (a round, or a whole pipeline step).
/// Partition key: /taskId. Id is deterministic so write-level redeliveries dedupe via 409.</summary>
public class UsageEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = "";
    [JsonPropertyName("boardId")] public string BoardId { get; set; } = "";
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("step")] public int Step { get; set; }
    [JsonPropertyName("round")] public int Round { get; set; }
    [JsonPropertyName("agentRoleId")] public string AgentRoleId { get; set; } = "";
    [JsonPropertyName("agentRoleName")] public string AgentRoleName { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("modelVersion")] public string? ModelVersion { get; set; }
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("usage")] public TokenUsage Usage { get; set; } = new();
    [JsonPropertyName("catalogVersion")] public string CatalogVersion { get; set; } = "";
    [JsonPropertyName("inputPerMillion")] public decimal InputPerMillion { get; set; }
    [JsonPropertyName("cachedInputPerMillion")] public decimal CachedInputPerMillion { get; set; }
    [JsonPropertyName("outputPerMillion")] public decimal OutputPerMillion { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
    [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }
    [JsonPropertyName("pricingMissing")] public bool PricingMissing { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public static string MakeId(string runId, int step, string invocationId, int round) =>
        $"{runId}:{step}:{invocationId}:{round}";
}
