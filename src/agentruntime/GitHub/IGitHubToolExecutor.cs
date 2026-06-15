using System.Text.Json;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

public interface IGitHubToolExecutor
{
    bool CanHandle(string toolName);
    Task<string> ExecuteAsync(string toolName, JsonElement args,
        GitHubRepoConnection? boardRepo, AgentRole role, CancellationToken ct);
}
