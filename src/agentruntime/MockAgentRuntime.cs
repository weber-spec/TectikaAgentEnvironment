using System.Collections.Concurrent;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Deterministic, no-Azure provisioner. Mutates the role's agent id/hash in place — same
/// contract as FoundryAgentRuntime — so the caller persists a populated FoundryAgentId.</summary>
public sealed class MockAgentProvisioner : IAgentProvisioner
{
    public Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(role.FoundryAgentId))
            role.FoundryAgentId = $"mock-agent-{role.Id}";
        role.FoundryAgentHash = AgentInstructionsHash.Compute(role.SystemPrompt, role.ModelOverride ?? "mock", TectikaToolSchema.Version);
        return Task.FromResult(new AgentSyncResult(role.FoundryAgentId, Synced: true));
    }

    public Task DeleteAgentAsync(string? foundryAgentId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Deterministic, no-Azure runtime. Stable thread per task; echoes the task into the artifact.</summary>
public sealed class MockAgentRuntime : IAgentRuntime
{
    private readonly ConcurrentDictionary<string, string> _threads = new();

    public Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(task.FoundryThreadId)) return Task.FromResult(task.FoundryThreadId!);
        var id = _threads.GetOrAdd(task.Id, _ => $"mock-thread-{task.Id}");
        return Task.FromResult(id);
    }

    public Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, IProjectExplorer explorer, CancellationToken ct = default)
    {
        var content =
            $"# {req.Role.DisplayName} output for: {req.Task.Title}\n\n" +
            $"(mock) Completed step {req.Step}.\n\n" +
            "## Brief Update\nMock turn complete.\n";
        var usage = new TokenUsage { Input = Math.Max(1, req.UserMessage.Length / 4), Output = Math.Max(1, content.Length / 4) };
        return Task.FromResult(new AgentRunOutcome(
            Status: AgentRunStatus.Completed,
            Content: content,
            ContentType: ArtifactContentType.Markdown,
            TokenUsage: usage,
            CompletionId: $"mock-cmpl-{req.RunId}-{req.Step}",
            BriefUpdate: "Mock turn complete."));
    }
}
