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

    [JsonPropertyName("workspaceContainerName")]
    public string? WorkspaceContainerName { get; set; }

    [JsonPropertyName("workspaceEndpoint")]
    public string? WorkspaceEndpoint { get; set; }

    [JsonPropertyName("workspaceStatus")]
    public BoardWorkspaceStatus WorkspaceStatus { get; set; } = BoardWorkspaceStatus.None;

    [JsonPropertyName("workspaceLastUsedAt")]
    public DateTimeOffset? WorkspaceLastUsedAt { get; set; }
}

public enum BoardWorkspaceStatus { None, Provisioning, Ready }
