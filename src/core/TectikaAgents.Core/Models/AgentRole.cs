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

    /// <summary>Which engine runs this role. Foundry (default — server-side REST to Azure AI Foundry) or
    /// ClaudeCode (the Claude Code CLI run inside the per-board ACI workspace container).</summary>
    [JsonPropertyName("executionEngine")]
    public ExecutionEngine ExecutionEngine { get; set; } = ExecutionEngine.Foundry;

    /// <summary>Key Vault secret NAME holding the Anthropic API key for a ClaudeCode role (the secret
    /// VALUE never lives on this model). Mirrors GitHubRepoConnection.PatSecretName. Null for Foundry.</summary>
    [JsonPropertyName("apiKeySecretName")]
    public string? ApiKeySecretName { get; set; }

    [JsonPropertyName("foundryAgentId")]
    public string? FoundryAgentId { get; set; }

    /// <summary>SHA-256 of (systemPrompt, model) at last successful Foundry sync. Null until first sync.</summary>
    [JsonPropertyName("foundryAgentHash")]
    public string? FoundryAgentHash { get; set; }

    [JsonPropertyName("tools")]
    public List<string> Tools { get; set; } = [];

    /// <summary>Catalog ids of MCP integrations this role is allowed to use (e.g. ["slack","notion"]).
    /// Drives which MCP tools are projected onto the Foundry agent definition.</summary>
    [JsonPropertyName("mcpServers")]
    public List<string> McpServers { get; set; } = [];

    /// <summary>Catalog ids (subset of <see cref="McpServers"/>) for which this role may call WRITE tools.
    /// Write tools are omitted from the agent definition unless their catalog id appears here.</summary>
    [JsonPropertyName("mcpWriteEnabled")]
    public List<string> McpWriteEnabled { get; set; } = [];

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

public enum ExecutionEngine { Foundry, ClaudeCode }

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
