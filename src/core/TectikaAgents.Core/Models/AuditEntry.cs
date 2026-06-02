using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class AuditEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    [JsonPropertyName("actorType")]
    public ActorType ActorType { get; set; }

    [JsonPropertyName("actorId")]
    public string ActorId { get; set; } = string.Empty;

    [JsonPropertyName("agentRoleId")]
    public string? AgentRoleId { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("identityUsed")]
    public string IdentityUsed { get; set; } = string.Empty;

    [JsonPropertyName("resource")]
    public AuditResource? Resource { get; set; }

    [JsonPropertyName("outcome")]
    public AuditOutcome Outcome { get; set; }

    [JsonPropertyName("tokenUsage")]
    public TokenUsage? TokenUsage { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }
}

public class AuditResource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

public enum ActorType { Agent, Human }
public enum AuditOutcome { Success, Denied, PendingApproval, Failed }
