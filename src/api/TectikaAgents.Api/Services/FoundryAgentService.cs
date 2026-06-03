using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Foundry Agent Service client — מפעיל סוכני AI ומחזיר תוצאה + artifact. It'll gonna be great!
/// Phase 1: HTTP calls ל-Foundry REST API.
/// </summary>
public class FoundryAgentService
{
    private readonly HttpClient _http;
    private readonly FoundrySettings _settings;
    private readonly ILogger<FoundryAgentService> _logger;

    public FoundryAgentService(HttpClient http, IOptions<FoundrySettings> settings, ILogger<FoundryAgentService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// מפעיל agent לפי תפקיד על task נתון ומחזיר artifact.
    /// יושלם בשלב חיבור Foundry Agent Service.
    /// </summary>
    public async Task<AgentRunResult> RunAgentAsync(
        AgentRole role,
        AgentTask task,
        IEnumerable<Artifact> upstreamArtifacts,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Invoking agent role {RoleId} for task {TaskId}", role.Id, task.Id);

        // TODO: Implement Foundry Agent Service API call
        // 1. Create thread with system prompt
        // 2. Add user message with task description + upstream artifacts as context
        // 3. Run agent with tools
        // 4. Stream response back via Service Bus events
        // 5. Return final artifact content

        await Task.CompletedTask;
        throw new NotImplementedException("Foundry Agent Service integration — implement in Week 3-4");
    }
}

public record AgentRunResult(
    string FoundryRunId,
    string ArtifactContent,
    ArtifactContentType ContentType,
    TokenUsage TokenUsage,
    List<string> InternalLogs);
