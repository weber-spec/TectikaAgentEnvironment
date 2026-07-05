using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>Shared compaction pipeline: summarize the chat-since-last-clear into the TaskBrief via one
/// throwaway agent turn, then reset the conversation (like /clear). On any failure it falls back to a
/// plain clear so state is never lost. Called by both the HTTP <c>CompactTrigger</c> and, mid-run, the
/// <c>CompactRunContextActivity</c> (so the steerable orchestrator can compact through a deterministic
/// activity boundary rather than doing this IO inline).</summary>
public sealed class RunCompactionService
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly IAgentRuntime _runtime;
    private readonly int _maxCompletionTokens;
    private readonly UsageRecorder _usage;
    private readonly string _defaultModel;
    private readonly string _provider;
    private readonly ILogger<RunCompactionService> _logger;

    public RunCompactionService(WorkflowCosmosService cosmos, IAgentRuntime runtime,
        IOptions<FoundrySettings> foundry, UsageRecorder usage, ILogger<RunCompactionService> logger)
    {
        _cosmos = cosmos;
        _runtime = runtime;
        _maxCompletionTokens = foundry.Value.MaxCompletionTokens;
        _usage = usage;
        _defaultModel = foundry.Value.DefaultModel;
        _provider = foundry.Value.IsOpenAiDirect ? "openai" : "azure-foundry";
        _logger = logger;
    }

    /// <summary>Summarize the conversation into the TaskBrief and reset the Foundry thread. Returns the
    /// summary brief (or "" when it fell back to a plain /clear). <paramref name="invocationId"/> keeps the
    /// usage event id deterministic so retries are idempotent.</summary>
    public async Task<string> CompactAsync(string boardId, string taskId, string invocationId, CancellationToken ct)
    {
        try
        {
            var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
            if (task is null) return "";

            var transcript = await BuildTranscriptAsync(taskId, task.ChatClearedAt, ct);
            var role = task.Assignee.Type == AssigneeType.Agent && !string.IsNullOrEmpty(task.Assignee.Id)
                ? await _cosmos.GetAgentRoleAsync(task.TenantId, task.Assignee.Id, ct)
                : null;

            if (role is not null && transcript.Length > 0)
            {
                var (summary, tokenUsage) = await SummarizeAsync(role, task, transcript, ct);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    var brief = summary.Trim();
                    await _cosmos.ResetTaskContextAsync(boardId, taskId, brief, ct);
                    _logger.LogInformation("[Compact] summarized task {TaskId} ({Len} chars)", taskId, brief.Length);

                    // Record the summarization call's token usage to the task's CURRENT session. RunId uses a
                    // stable compaction correlation string (no workflow run owns this), keeping event ids
                    // deterministic so retries are idempotent.
                    var compactRunId = $"compact-{taskId}";
                    var model = role.ModelOverride ?? _defaultModel;
                    await _usage.RecordAsync(new UsageRecorder.Attribution(
                        TenantId: task.TenantId, BoardId: boardId, TaskId: taskId,
                        RunId: compactRunId, Step: 0, Round: 0,
                        InvocationId: invocationId,
                        AgentRoleId: "system:compaction", AgentRoleName: "Compaction",
                        Provider: _provider, Model: model, ModelVersion: null,
                        SessionId: task.UsageSessionId ?? compactRunId),
                        tokenUsage, ct);

                    return brief;
                }
            }

            await _cosmos.ResetTaskContextAsync(boardId, taskId, "", ct);   // fallback = /clear
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Compact] failed task {TaskId} — falling back to clear", taskId);
            try { await _cosmos.ResetTaskContextAsync(boardId, taskId, "", ct); } catch { /* best effort */ }
            return "";
        }
    }

    private async Task<string> BuildTranscriptAsync(string taskId, DateTimeOffset? clearedAt, CancellationToken ct)
    {
        var events = await _cosmos.GetRunEventsAsync(taskId, null, ct);
        var sb = new StringBuilder();
        foreach (var e in events.Where(e => clearedAt is null || e.Timestamp > clearedAt))
        {
            if (e.Kind == RunEventKind.UserMessage) sb.AppendLine($"User: {e.Title}");
            else if (e.Kind == RunEventKind.AgentMessage) sb.AppendLine($"Agent: {e.Detail ?? e.Title}");
        }
        return sb.ToString();
    }

    private async Task<(string Content, TokenUsage TokenUsage)> SummarizeAsync(AgentRole role, AgentTask task, string transcript, CancellationToken ct)
    {
        var throwaway = new AgentTask { Id = task.Id, BoardId = task.BoardId, TenantId = task.TenantId, Title = task.Title };
        var thread = await _runtime.EnsureThreadAsync(throwaway, ct);   // fresh conversation, discarded after
        var prompt = "Summarize the key decisions, current state, and open items of this conversation as a " +
                     "short brief (a few concise bullet points). Do not use tools.\n\nConversation:\n" + transcript;
        var outcome = await _runtime.RunTurnAsync(
            new AgentRunRequest(role, throwaway, thread, prompt, _maxCompletionTokens, "compact", 0),
            new NoopProjectExplorer(), ct);
        return outcome.Status == AgentRunStatus.Completed
            ? (outcome.Content, outcome.TokenUsage)
            : ("", outcome.TokenUsage);
    }
}
