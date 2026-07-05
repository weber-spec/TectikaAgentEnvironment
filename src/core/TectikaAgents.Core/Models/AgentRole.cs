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

    /// <summary>Key Vault secret NAME holding the Claude credential for a ClaudeCode role (the secret
    /// VALUE never lives on this model). Mirrors GitHubRepoConnection.PatSecretName. Null for Foundry.</summary>
    [JsonPropertyName("apiKeySecretName")]
    public string? ApiKeySecretName { get; set; }

    /// <summary>How a ClaudeCode role authenticates to Anthropic. ApiKey → pay-as-you-go API key
    /// (ANTHROPIC_API_KEY); OAuthToken → a Pro/Max subscription token from `claude setup-token`
    /// (CLAUDE_CODE_OAUTH_TOKEN). Determines which env var the runtime injects. Ignored for Foundry.</summary>
    [JsonPropertyName("claudeAuth")]
    public ClaudeAuthMode ClaudeAuth { get; set; } = ClaudeAuthMode.ApiKey;

    [JsonPropertyName("foundryAgentId")]
    public string? FoundryAgentId { get; set; }

    /// <summary>SHA-256 of (systemPrompt, model) at last successful Foundry sync. Null until first sync.</summary>
    [JsonPropertyName("foundryAgentHash")]
    public string? FoundryAgentHash { get; set; }

    [JsonPropertyName("tools")]
    public List<string> Tools { get; set; } = [];

    /// <summary>Tenant connections (agent-tool category) this role may use. Each reference denormalizes the
    /// connection's <see cref="AgentConnectionRef.CatalogId"/> so the board-independent Foundry projection +
    /// instructions hash need no lookup. A connection is only usable at runtime if the BOARD also enabled it.</summary>
    [JsonPropertyName("connections")]
    public List<AgentConnectionRef> Connections { get; set; } = [];

    /// <summary>The tenant "model" connection powering this role (Foundry system connection, or an Anthropic
    /// connection). Optional/forward-looking: the runtime still uses <see cref="ExecutionEngine"/> today.</summary>
    [JsonPropertyName("modelConnectionId")]
    public string? ModelConnectionId { get; set; }

    /// <summary>Foundry built-in tool ids this role enables (e.g. ["code_interpreter"]). Projected into the
    /// Foundry agent definition; ignored for Claude Code (which has native tools). Only globally-enabled,
    /// agent-selectable tools should appear here.</summary>
    [JsonPropertyName("foundryTools")]
    public List<string> FoundryTools { get; set; } = [];

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

/// <summary>An agent's reference to a tenant connection it may use, with the write opt-in. <see cref="CatalogId"/>
/// is denormalized from the connection so tool projection/hashing stay board-independent and lookup-free.</summary>
public class AgentConnectionRef
{
    [JsonPropertyName("connectionId")] public string ConnectionId { get; set; } = string.Empty;
    [JsonPropertyName("catalogId")]    public string CatalogId { get; set; } = string.Empty;
    [JsonPropertyName("writeEnabled")] public bool WriteEnabled { get; set; }
}

public enum ExecutionEngine { Foundry, ClaudeCode }

/// <summary>How a ClaudeCode role authenticates: a pay-as-you-go API key, or a Pro/Max subscription
/// OAuth token (from `claude setup-token`).</summary>
public enum ClaudeAuthMode { ApiKey, OAuthToken }

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
