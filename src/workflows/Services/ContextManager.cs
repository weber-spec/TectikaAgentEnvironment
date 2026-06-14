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
    private readonly int _maxInputTokens;

    public ContextManager(IOptions<LoggingSettings> logging, IOptions<FoundrySettings> foundry, ILogger<ContextManager> logger)
    {
        _logger = logger;
        _logSensitive = logging.Value.LogSensitiveContent;
        _maxInputTokens = foundry.Value.MaxInputTokens > 0 ? foundry.Value.MaxInputTokens : DefaultMaxInputTokens;
    }

    /// <summary>Most-recent brief lines kept inline; older history is trimmed (full brief stays in Cosmos).</summary>
    private const int BriefTailLines = 12;
    private const int DefaultMaxInputTokens = 100_000;

    /// <summary>Assemble the user-content string for an agent turn (no system prompt — that's the
    /// Foundry agent's instructions). Includes the per-task prompt, a trimmed brief, and direct
    /// upstream artifacts up to a token budget; beyond the budget, upstream falls back to summaries
    /// the agent can expand via the get_artifact tool. Control flow (interaction/approval/revision/
    /// brief updates) is handled by tools, not prose markers.</summary>
    public static string Assemble(AgentRole role, AgentTask task, Board board, IReadOnlyList<Artifact> upstream,
        int maxInputTokens = DefaultMaxInputTokens)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Project: {board.Name}");
        if (!string.IsNullOrWhiteSpace(board.Description)) sb.AppendLine(board.Description);
        sb.AppendLine();
        sb.AppendLine($"## Task: {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description)) sb.AppendLine(task.Description);

        // Per-task instruction (layered on top of the role persona).
        if (!string.IsNullOrWhiteSpace(task.Prompt))
        {
            sb.AppendLine("\n## Your instructions for this task");
            sb.AppendLine(task.Prompt!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(task.TaskBrief))
            sb.AppendLine($"\n## Task brief (recent history)\n{TrimBrief(task.TaskBrief)}");

        // Exploration nudge — the agent has board-scoped tools and should use them.
        sb.AppendLine("\nYou can and should explore this board before doing significant work: call");
        sb.AppendLine("get_board_overview to see the whole project, search_tasks / get_task to find related");
        sb.AppendLine("work, and get_artifact to read any task's full output. Don't guess when you can look.");

        // Direct upstream: full content within the token budget, then summaries (expandable via get_artifact).
        var used = EstimateTokens(sb.ToString());
        foreach (var art in upstream)
        {
            var fullCost = EstimateTokens(art.Content);
            if (used + fullCost <= maxInputTokens)
            {
                sb.AppendLine($"\n### Input from task {art.TaskId} ({art.ContentType}):");
                sb.AppendLine("```");
                sb.AppendLine(art.Content);
                sb.AppendLine("```");
                used += fullCost;
            }
            else
            {
                var summary = string.IsNullOrWhiteSpace(art.Summary) ? "(no summary available)" : art.Summary!.Trim();
                sb.AppendLine($"\n### Input from task {art.TaskId} (summary — over context budget):");
                sb.AppendLine(summary);
                sb.AppendLine($"(Full content available via the get_artifact tool: taskId={art.TaskId}.)");
                used += EstimateTokens(summary);
            }
        }

        sb.AppendLine("\nComplete the task. Be thorough and production-ready. Your final message is the deliverable.");
        return sb.ToString();
    }

    /// <summary>Rough token estimate (~4 chars/token) — good enough for budgeting decisions.</summary>
    private static int EstimateTokens(string s) => string.IsNullOrEmpty(s) ? 0 : s.Length / 4;

    private static string TrimBrief(string brief)
    {
        var lines = brief.Split('\n');
        return lines.Length <= BriefTailLines
            ? brief
            : string.Join('\n', lines[^BriefTailLines..]);
    }

    /// <summary>Instance entry point used by the activity (kept async for future retrieval/summarize steps).</summary>
    public Task<string> BuildUserContentAsync(AgentRole role, AgentTask task, Board board,
        IReadOnlyList<Artifact> upstream, CancellationToken ct = default)
    {
        var content = Assemble(role, task, board, upstream, _maxInputTokens);
        _logger.LogDebug("[Context] built context for task {TaskId} role {RoleId} upstream={UpstreamCount} content={Content}",
            task.Id, role.Id, upstream.Count, SensitiveContent.Format(content, _logSensitive));
        return Task.FromResult(content);
    }
}
