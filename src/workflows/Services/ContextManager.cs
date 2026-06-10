using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Observability;

namespace TectikaAgents.Workflows.Services;

/// <summary>
/// Builds the user-content string for an agent turn. The agent carries the system prompt as its
/// Foundry instructions, so this assembles only the user message: task + task brief + upstream artifacts.
/// (Token-budgeting / retrieval / summarization is deferred to a later phase.)
/// </summary>
public class ContextManager
{
    private readonly ILogger<ContextManager> _logger;
    private readonly bool _logSensitive;

    public ContextManager(IOptions<LoggingSettings> logging, ILogger<ContextManager> logger)
    {
        _logger = logger;
        _logSensitive = logging.Value.LogSensitiveContent;
    }

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
    {
        var content = Assemble(role, task, board, upstream);
        _logger.LogDebug("[Context] built context for task {TaskId} role {RoleId} upstream={UpstreamCount} content={Content}",
            task.Id, role.Id, upstream.Count, SensitiveContent.Format(content, _logSensitive));
        return Task.FromResult(content);
    }
}
