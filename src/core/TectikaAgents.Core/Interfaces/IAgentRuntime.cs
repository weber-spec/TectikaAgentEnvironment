using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Runs agent turns against Foundry threads (used by the Workflows activity).</summary>
public interface IAgentRuntime
{
    /// <summary>Return the task's thread id, creating + persisting one if missing.</summary>
    Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default);

    /// <summary>Run one server-side turn and return its terminal outcome.</summary>
    Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, CancellationToken ct = default);
}
