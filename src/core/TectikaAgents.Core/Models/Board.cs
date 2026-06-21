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

    /// <summary>
    /// Strips a trailing ".git" suffix from a repository name. Clone URLs
    /// (e.g. https://github.com/owner/repo.git) carry the suffix, but the
    /// GitHub REST API expects the bare name — passing "repo.git" yields a 404.
    /// </summary>
    public static string NormalizeRepoName(string repo)
    {
        if (string.IsNullOrEmpty(repo)) return repo;
        return repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repo[..^4]
            : repo;
    }
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
}
