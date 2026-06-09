using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>
/// Builds the LLM context for an agent turn under a token budget.
/// Assembles: system prompt + project brief + board overview + task brief + upstream artifacts.
/// </summary>
public class ContextManager(
    WorkflowCosmosService cosmos,
    ILogger<ContextManager> logger,
    IOptions<FoundrySettings> foundry)
{
    private readonly FoundrySettings _foundry = foundry.Value;

    public async Task<List<object>> BuildContextAsync(
        AgentRole role,
        AgentTask task,
        Board board,
        List<Artifact> upstreamArtifacts,
        CancellationToken ct = default)
    {
        var allBoardTasks = await cosmos.GetBoardTasksAsync(board.Id, ct);

        var budget = _foundry.MaxInputTokens - EstimateTokens(role.SystemPrompt);

        var sb = new StringBuilder();

        // ── Project Brief ────────────────────────────────────────────────────
        sb.AppendLine($"## Project: {board.Name}");
        if (!string.IsNullOrWhiteSpace(board.Description))
            sb.AppendLine(board.Description);
        sb.AppendLine();

        // ── Board Overview ───────────────────────────────────────────────────
        sb.AppendLine("## Board Overview");
        foreach (var t in allBoardTasks)
        {
            var lastBrief = GetLastBriefLine(t.TaskBrief);
            sb.AppendLine(string.IsNullOrEmpty(lastBrief)
                ? $"- {t.Title} [{t.Status}]"
                : $"- {t.Title} [{t.Status}]: {lastBrief}");
        }
        sb.AppendLine();

        // ── This Task ────────────────────────────────────────────────────────
        sb.AppendLine($"## Your Task");
        sb.AppendLine($"### {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description))
            sb.AppendLine(task.Description);
        sb.AppendLine();

        // ── TaskBrief ────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(task.TaskBrief))
        {
            sb.AppendLine("## Context from prior runs on this task");
            sb.AppendLine(task.TaskBrief);
            sb.AppendLine();
        }

        // ── Upstream Artifacts (under token budget) ──────────────────────────
        budget -= EstimateTokens(sb.ToString());

        if (upstreamArtifacts.Count > 0)
        {
            sb.AppendLine("## Inputs from upstream tasks");
            foreach (var art in upstreamArtifacts)
            {
                var header = $"\n### Input ({art.ContentType}) — Task {art.TaskId}:\n";
                var fullContent = $"```\n{art.Content}\n```";
                var fullTokens = EstimateTokens(header + fullContent);

                if (fullTokens <= budget)
                {
                    sb.Append(header);
                    sb.AppendLine(fullContent);
                    budget -= fullTokens;
                }
                else if (!string.IsNullOrEmpty(art.Summary))
                {
                    var summaryBlock = $"{header}*Summary:* {art.Summary}\n";
                    sb.Append(summaryBlock);
                    budget -= EstimateTokens(summaryBlock);
                    logger.LogInformation("Used artifact summary for task {TaskId} (full content exceeded budget)", art.TaskId);
                }
                else
                {
                    // Truncate to threshold
                    var maxChars = _foundry.SummaryThresholdTokens * 4;
                    var truncated = art.Content.Length > maxChars
                        ? art.Content[..maxChars] + "\n...(truncated)"
                        : art.Content;
                    var truncBlock = $"{header}```\n{truncated}\n```\n";
                    sb.Append(truncBlock);
                    budget -= EstimateTokens(truncBlock);
                    logger.LogInformation("Truncated artifact for task {TaskId} (no summary available)", art.TaskId);
                }

                if (budget <= 0)
                {
                    logger.LogWarning("Token budget exhausted after {Count} upstream artifacts", upstreamArtifacts.IndexOf(art) + 1);
                    break;
                }
            }
            sb.AppendLine();
        }

        // ── Instructions ─────────────────────────────────────────────────────
        sb.AppendLine("---");
        sb.AppendLine("Complete the task. Be thorough and production-ready.");
        sb.AppendLine();
        sb.AppendLine("At the end of your response, include these two sections EXACTLY:");
        sb.AppendLine();
        sb.AppendLine("## Brief Update");
        sb.AppendLine("<one sentence: what you did / decided / any important finding or blocker>");
        sb.AppendLine();
        sb.AppendLine("## Artifact Summary");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"summary\": \"<2-3 sentences describing what was produced>\",");
        sb.AppendLine("  \"keyDecisions\": [\"<decision1>\"],");
        sb.AppendLine("  \"interfaces\": [\"<public API / contract exposed>\"],");
        sb.AppendLine("  \"constraints\": [\"<important constraint or assumption>\"]");
        sb.AppendLine("}");
        sb.AppendLine("```");

        return
        [
            new { role = "system", content = role.SystemPrompt },
            new { role = "user",   content = sb.ToString() }
        ];
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
        => Task.FromResult(Assemble(role, task, board, upstream));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int EstimateTokens(string text) => text.Length / 4;

    private static string GetLastBriefLine(string taskBrief)
    {
        if (string.IsNullOrWhiteSpace(taskBrief)) return "";
        return taskBrief.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
    }
}
