using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class AgentRole
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("systemPrompt")]
    public string SystemPrompt { get; set; } = string.Empty;

    [JsonPropertyName("foundryAgentId")]
    public string? FoundryAgentId { get; set; }

    /// <summary>SHA-256 of (systemPrompt, model) at last successful Foundry sync. Null until first sync.</summary>
    [JsonPropertyName("foundryAgentHash")]
    public string? FoundryAgentHash { get; set; }

    [JsonPropertyName("tools")]
    public List<string> Tools { get; set; } = [];

    [JsonPropertyName("mcpServers")]
    public List<string> McpServers { get; set; } = [];

    [JsonPropertyName("permissions")]
    public AgentPermissions Permissions { get; set; } = new();

    [JsonPropertyName("escalateTo")]
    public string? EscalateTo { get; set; }

    [JsonPropertyName("modelOverride")]
    public string? ModelOverride { get; set; }

    [JsonPropertyName("githubPermissions")]
    public GitHubPermissions? GitHubPermissions { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class GitHubPermissions
{
    [JsonPropertyName("canRead")]
    public bool CanRead { get; set; }
}

public class AgentPermissions
{
    [JsonPropertyName("canUseWorkspace")]
    public bool CanUseWorkspace { get; set; }

    [JsonPropertyName("canPushCode")]
    public bool CanPushCode { get; set; }

    [JsonPropertyName("canDeploy")]
    public bool CanDeploy { get; set; }

    [JsonPropertyName("requiresOboFor")]
    public List<string> RequiresOboFor { get; set; } = [];

    [JsonPropertyName("requiresApprovalFor")]
    public List<string> RequiresApprovalFor { get; set; } = [];
}
