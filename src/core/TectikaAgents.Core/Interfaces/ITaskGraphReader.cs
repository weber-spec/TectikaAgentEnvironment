using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>
/// The read side of a board's Dependency graph — exactly what <see cref="Scheduling.TaskStartGate"/>
/// needs to decide whether a task may start, and nothing more. Both persistence layers (the API's
/// ICosmosDbService and the workflows' WorkflowCosmosService) implement it, so the eligibility rule
/// has a single home in Core instead of one copy per cascade.
/// </summary>
public interface ITaskGraphReader
{
    Task<AgentTask?> GetTaskAsync(string boardId, string taskId, CancellationToken ct = default);

    /// <summary>Sources of incoming Dependency edges. QaFeedback edges are excluded — they are the
    /// loop-back arc of a QA cycle, not a prerequisite.</summary>
    Task<IReadOnlyList<string>> GetUpstreamTaskIdsAsync(string boardId, string taskId, CancellationToken ct = default);

    /// <summary>Targets of outgoing Dependency edges. QaFeedback edges are excluded.</summary>
    Task<IReadOnlyList<string>> GetDownstreamTaskIdsAsync(string boardId, string taskId, CancellationToken ct = default);
}
