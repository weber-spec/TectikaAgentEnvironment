using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Runs agent turns against Foundry threads (used by the Workflows activity).</summary>
public interface IAgentRuntime
{
    /// <summary>Return the task's thread id, creating + persisting one if missing.</summary>
    Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default);

    /// <summary>Run one steerable turn (a multi-round tool-loop) and return its terminal outcome.
    /// The board-scoped <paramref name="explorer"/> backs the agent's exploration tools.</summary>
    Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, IProjectExplorer explorer, CancellationToken ct = default);

    /// <summary>Run exactly ONE model⇄tool round (fine-grained Shape B — the orchestrator owns
    /// iteration). Submits any pending tool outputs (+ optional user input) and executes the round's
    /// explore tools via <paramref name="explorer"/>.</summary>
    Task<RoundOutcome> RunRoundAsync(RoundRequest req, IProjectExplorer explorer, CancellationToken ct = default);
}
