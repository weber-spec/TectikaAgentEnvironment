using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Scheduling;

/// <summary>Why a task may not be started automatically.</summary>
public enum TaskStartBlock
{
    None,
    NotFound,
    NotBacklog,
    NoAgentAssignee,
    /// <summary>A Dependency parent is missing or not Done yet.</summary>
    UpstreamNotDone,
}

/// <param name="BlockingUpstreamId">The first unsatisfied parent, when <see cref="Block"/> is UpstreamNotDone.</param>
/// <param name="UpstreamMissing">True when that parent's document is gone entirely (a dangling edge) rather
/// than merely unfinished — a data-integrity problem, not a normal wait.</param>
public readonly record struct TaskStartDecision(
    bool CanStart,
    TaskStartBlock Block,
    string? BlockingUpstreamId = null,
    bool UpstreamMissing = false)
{
    public static readonly TaskStartDecision Allowed = new(true, TaskStartBlock.None);
}

/// <summary>
/// The single home of the rule that decides whether a task may be started automatically:
/// it exists, it is Backlog, it is assigned to an agent, and every Dependency parent is Done.
///
/// Three callers share it: the Durable cascade (UpdateRunStatusActivity), the in-process cascade
/// (TasksController), and Run Board's server-side re-validation (RunStartService). They previously
/// each carried their own copy of the rule and had already drifted apart.
/// </summary>
public static class TaskStartGate
{
    /// <summary>
    /// True when every Dependency parent exists and is Done (vacuously true for a root).
    /// A parent whose document is gone — a dangling edge — counts as unsatisfied: we cannot prove
    /// its work was done, so we refuse to start on top of it.
    /// </summary>
    public static async Task<(bool Satisfied, string? BlockingUpstreamId, bool Missing)> DependenciesSatisfiedAsync(
        ITaskGraphReader graph, string boardId, string taskId, CancellationToken ct = default)
    {
        foreach (var upstreamId in await graph.GetUpstreamTaskIdsAsync(boardId, taskId, ct))
        {
            var upstream = await graph.GetTaskAsync(boardId, upstreamId, ct);
            if (upstream is null) return (false, upstreamId, true);
            if (upstream.Status != AgentTaskStatus.Done) return (false, upstreamId, false);
        }
        return (true, null, false);
    }

    public static async Task<TaskStartDecision> EvaluateAsync(
        ITaskGraphReader graph, string boardId, string taskId, CancellationToken ct = default)
    {
        var task = await graph.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return new TaskStartDecision(false, TaskStartBlock.NotFound);
        return await EvaluateAsync(graph, boardId, task, ct);
    }

    /// <summary>Same rule, for callers that already hold the task document.</summary>
    public static async Task<TaskStartDecision> EvaluateAsync(
        ITaskGraphReader graph, string boardId, AgentTask task, CancellationToken ct = default)
    {
        if (task.Status != AgentTaskStatus.Backlog)
            return new TaskStartDecision(false, TaskStartBlock.NotBacklog);

        if (task.Assignee.Type != AssigneeType.Agent || string.IsNullOrEmpty(task.Assignee.Id))
            return new TaskStartDecision(false, TaskStartBlock.NoAgentAssignee);

        var (satisfied, blocking, missing) = await DependenciesSatisfiedAsync(graph, boardId, task.Id, ct);
        return satisfied
            ? TaskStartDecision.Allowed
            : new TaskStartDecision(false, TaskStartBlock.UpstreamNotDone, blocking, missing);
    }
}
