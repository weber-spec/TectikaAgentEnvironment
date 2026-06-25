using System.Text.Json.Serialization;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Usage;

/// <summary>Ledger-truth usage rollup for a single agent role, aggregated from UsageEvents.
/// Drives the analytics "tokens by agent" chart and leaderboard (replaces the brittle
/// runs→tasks→assignee derivation from WorkflowRun.TotalTokens).</summary>
public class AgentUsage
{
    [JsonPropertyName("agentRoleId")] public string AgentRoleId { get; set; } = "";
    [JsonPropertyName("agentRoleName")] public string AgentRoleName { get; set; } = "";
    [JsonPropertyName("tokens")] public TokenUsage Tokens { get; set; } = new();
    [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }
    [JsonPropertyName("eventCount")] public int EventCount { get; set; }
}
