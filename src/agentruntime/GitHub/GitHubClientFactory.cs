using Octokit;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

/// <summary>Builds an authenticated Octokit client for a board's repo by resolving
/// its PAT through the secret provider. Shared by the read service and the tool executor.</summary>
public static class GitHubClientFactory
{
    public static async Task<GitHubClient> CreateAsync(ISecretProvider secrets, GitHubRepoConnection repo, CancellationToken ct)
    {
        var pat = await secrets.GetSecretAsync(repo.PatSecretName, ct);
        return new GitHubClient(new ProductHeaderValue("TectikaAgents"))
        {
            Credentials = new Credentials(pat),
        };
    }
}
