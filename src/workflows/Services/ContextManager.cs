using System.Text;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>
/// Builds the user-content string for an agent turn. The agent carries the system prompt as its
/// Foundry instructions, so this assembles only the user message: task + task brief + upstream artifacts.
/// (Token-budgeting / retrieval / summarization is deferred to a later phase.)
/// </summary>
public class ContextManager
{
    /// <summary>Assemble the user-content string for an agent turn (no system prompt).
    /// Board.Goal/MasterPlan are added in Phase 2; not referenced here.</summary>
    public static string Assemble(AgentRole role, AgentTask task, Board board, IReadOnlyList<Artifact> upstream)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Task: {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description)) sb.AppendLine(task.Description);
        if (!string.IsNullOrWhiteSpace(task.TaskBrief)) sb.AppendLine($"\n## Task brief (history)\n{task.TaskBrief}");
        foreach (var art in upstream)
        {
            sb.AppendLine($"\n### Input ({art.ContentType}):");
            sb.AppendLine("```");
            sb.AppendLine(art.Content);
            sb.AppendLine("```");
        }
        sb.AppendLine("\nComplete the task. Be thorough and production-ready.");
        sb.AppendLine("End with a one-line `## Brief Update`.");
        return sb.ToString();
    }

    /// <summary>Instance entry point used by the activity (kept async for future retrieval/summarize steps).</summary>
    public Task<string> BuildUserContentAsync(AgentRole role, AgentTask task, Board board,
        IReadOnlyList<Artifact> upstream, CancellationToken ct = default)
        => Task.FromResult(Assemble(role, task, board, upstream));
}
