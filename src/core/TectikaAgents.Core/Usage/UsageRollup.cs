using System.Text.Json.Serialization;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Usage;

/// <summary>Mutable accumulator of tokens + cost over some scope. Note: TokenUsage.Total is computed,
/// so we store the component fields and let Total derive.</summary>
public class UsageBucket
{
    [JsonPropertyName("tokens")] public TokenUsage Tokens { get; set; } = new();
    [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }
    [JsonPropertyName("eventCount")] public int EventCount { get; set; }

    public void Add(TokenUsage u, decimal costUsd)
    {
        Tokens.Input += u.Input;
        Tokens.CachedInput += u.CachedInput;
        Tokens.Output += u.Output;
        Tokens.Reasoning += u.Reasoning;
        CostUsd += costUsd;
        EventCount += 1;
    }
}

public class SessionBucket : UsageBucket
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("since")] public DateTimeOffset Since { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Per-model breakdown for THIS session only, keyed by "provider/model".
    /// Resets (empty) on /clear, so the UI's Session view shows a true session breakdown.</summary>
    [JsonPropertyName("perModel")] public Dictionary<string, UsageBucket> PerModel { get; set; } = new();
}

public enum UsageScope { Project, Board, Task }

/// <summary>Materialized rollup at project/board/task scope. Partition key: /tenantId.</summary>
public class UsageRollup
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = "";
    [JsonPropertyName("scope")] public UsageScope Scope { get; set; }
    [JsonPropertyName("scopeId")] public string ScopeId { get; set; } = "";
    [JsonPropertyName("lifetime")] public UsageBucket Lifetime { get; set; } = new();

    /// <summary>Per-model breakdown keyed by "provider/model".</summary>
    [JsonPropertyName("perModel")] public Dictionary<string, UsageBucket> PerModel { get; set; } = new();

    /// <summary>Task scope only — usage since the last /clear. Null for project/board scope.</summary>
    [JsonPropertyName("currentSession")] public SessionBucket? CurrentSession { get; set; }

    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string ProjectId(string tenantId) => $"project:{tenantId}";
    public static string BoardId(string boardId) => $"board:{boardId}";
    public static string TaskId(string taskId) => $"task:{taskId}";
    public static string ModelKey(string provider, string model) => $"{provider}/{model}";
}
