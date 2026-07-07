using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class GitHubRepoConnection
{
    [JsonPropertyName("repoUrl")]
    public string RepoUrl { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;

    [JsonPropertyName("patSecretName")]
    public string PatSecretName { get; set; } = string.Empty;
}

public class Board
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    public List<string> Columns { get; set; } = ["backlog", "in-progress", "review", "done"];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("github")]
    public GitHubRepoConnection? GitHub { get; set; }

    /// <summary>Tenant connections enabled on this board (with per-board binding config, e.g. the GitHub repo).
    /// A connection defined in the tenant registry is only usable by this board's agents once bound here.</summary>
    [JsonPropertyName("connections")]
    public List<BoardConnectionBinding> Connections { get; set; } = [];

    [JsonPropertyName("workspaceContainerName")]
    public string? WorkspaceContainerName { get; set; }

    [JsonPropertyName("workspaceEndpoint")]
    public string? WorkspaceEndpoint { get; set; }

    [JsonPropertyName("workspaceStatus")]
    public BoardWorkspaceStatus WorkspaceStatus { get; set; } = BoardWorkspaceStatus.None;

    [JsonPropertyName("workspaceLastUsedAt")]
    public DateTimeOffset? WorkspaceLastUsedAt { get; set; }

    /// <summary>Backing board auto-created to host channel agent chat for a DM / non-board channel.
    /// Hidden from the Boards list — it exists only to give the run pipeline a board context.</summary>
    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }
}

/// <summary>Binds a tenant connection to a board (enables it) plus per-board config — e.g. for GitHub the
/// selected repo ({ owner, repo, repoUrl }); empty for tools whose config is entirely on the connection.</summary>
public class BoardConnectionBinding
{
    [JsonPropertyName("connectionId")] public string ConnectionId { get; set; } = string.Empty;
    [JsonPropertyName("config")]       public Dictionary<string, string> Config { get; set; } = new();
}

public enum BoardWorkspaceStatus { None, Provisioning, Ready }
