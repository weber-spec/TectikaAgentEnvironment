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
        sb.AppendLine($"## Project: {board.Name}");
        if (!string.IsNullOrWhiteSpace(board.Description)) sb.AppendLine(board.Description);
        sb.AppendLine();
        sb.AppendLine($"## Task: {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description)) sb.AppendLine(task.Description);
        if (!string.IsNullOrWhiteSpace(task.TaskBrief)) sb.AppendLine($"\n## Task brief (history)\n{task.TaskBrief}");
        if (!string.IsNullOrWhiteSpace(task.TaskBrief) && task.TaskBrief.Contains("[Human,"))
        {
            sb.AppendLine("\n## Instruction: Human Decisions");
            sb.AppendLine("The Task brief above contains decisions made by a human during this pipeline run.");
            sb.AppendLine("Your artifact MUST begin with a `## Decisions Made` section that explicitly lists every human decision/selection/answer. Example:");
            sb.AppendLine("  ## Decisions Made");
            sb.AppendLine("  - Selected: Marriott Grand Hotel, $200/night (user chose)");
            sb.AppendLine("  - Check-in: June 15 (user confirmed)");
            sb.AppendLine("This section is required so downstream agents know what was decided.");
        }
        foreach (var art in upstream)
        {
            sb.AppendLine($"\n### Input ({art.ContentType}):");
            sb.AppendLine("```");
            sb.AppendLine(art.Content);
            sb.AppendLine("```");
        }
        if (upstream.Any(a => a.Content.Contains("## Decisions Made", StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine("\n⚠ Note: One or more upstream inputs above contain a `## Decisions Made` section.");
            sb.AppendLine("These are confirmed human decisions. Do NOT re-ask for this information.");
            sb.AppendLine("Use these decisions directly in your task output.");
        }
        sb.AppendLine("\nComplete the task. Be thorough and production-ready.");
        sb.AppendLine("End with a one-line `## Brief Update`.");
        sb.AppendLine("\nIf you cannot proceed without human input, end your response with ## INTERACTION_REQUIRED followed by a JSON block (examples below).");
        sb.AppendLine("- Question (need a free-text or multiple-choice answer):");
        sb.AppendLine("  ## INTERACTION_REQUIRED");
        sb.AppendLine("  { \"type\": \"Question\", \"actionDescription\": \"<one-liner>\", \"question\": \"<your question>\", \"questionOptions\": [\"opt1\",\"opt2\"] }");
        sb.AppendLine("  (omit questionOptions for a free-text answer)");
        sb.AppendLine("- Selection (user picks one item from a list you provide):");
        sb.AppendLine("  ## INTERACTION_REQUIRED");
        sb.AppendLine("  { \"type\": \"Selection\", \"actionDescription\": \"<one-liner>\", \"items\": [{\"title\":\"...\",\"subtitle\":\"...\",\"price\":\"...\",\"details\":[\"...\"],\"link\":\"...\"}] }");
        sb.AppendLine("- Approval (need explicit approve/reject before continuing):");
        sb.AppendLine("  ## INTERACTION_REQUIRED");
        sb.AppendLine("  { \"type\": \"Approval\", \"actionDescription\": \"<describe what needs approval>\" }");
        sb.AppendLine("Only use INTERACTION_REQUIRED when the task genuinely cannot continue without human input.");
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
