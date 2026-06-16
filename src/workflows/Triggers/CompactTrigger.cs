using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Triggers;

/// <summary>/compact — summarize the chat into the TaskBrief via one agent turn, then reset the
/// conversation (like /clear). On any failure it falls back to a plain clear so state is never lost.</summary>
public class CompactTrigger
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly TectikaAgents.Core.Interfaces.IAgentRuntime _runtime;
    private readonly int _maxCompletionTokens;
    private readonly ILogger<CompactTrigger> _logger;

    public CompactTrigger(WorkflowCosmosService cosmos, TectikaAgents.Core.Interfaces.IAgentRuntime runtime,
        IOptions<FoundrySettings> foundry, ILogger<CompactTrigger> logger)
    {
        _cosmos = cosmos;
        _runtime = runtime;
        _maxCompletionTokens = foundry.Value.MaxCompletionTokens;
        _logger = logger;
    }

    [Function(nameof(Compact))]
    public async Task<HttpResponseData> Compact(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "pipelines/compact/{boardId}/{taskId}")] HttpRequestData req,
        string boardId, string taskId, FunctionContext context)
    {
        var ct = context.CancellationToken;
        var summarized = false;
        try
        {
            var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
            if (task is null) return await Json(req, new { summarized = false }, System.Net.HttpStatusCode.NotFound);

            var transcript = await BuildTranscriptAsync(taskId, task.ChatClearedAt, ct);
            var role = task.Assignee.Type == AssigneeType.Agent && !string.IsNullOrEmpty(task.Assignee.Id)
                ? await _cosmos.GetAgentRoleAsync(task.TenantId, task.Assignee.Id, ct)
                : null;

            if (role is not null && transcript.Length > 0)
            {
                var summary = await SummarizeAsync(role, task, transcript, ct);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    await _cosmos.ResetTaskContextAsync(boardId, taskId, summary.Trim(), ct);
                    summarized = true;
                    _logger.LogInformation("[Compact] summarized task {TaskId} ({Len} chars)", taskId, summary.Length);
                }
            }

            if (!summarized) await _cosmos.ResetTaskContextAsync(boardId, taskId, "", ct);   // fallback = /clear
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Compact] failed task {TaskId} — falling back to clear", taskId);
            try { await _cosmos.ResetTaskContextAsync(boardId, taskId, "", ct); } catch { /* best effort */ }
            summarized = false;
        }

        return await Json(req, new { summarized }, System.Net.HttpStatusCode.OK);
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

    private async Task<string> SummarizeAsync(AgentRole role, AgentTask task, string transcript, CancellationToken ct)
    {
        var throwaway = new AgentTask { Id = task.Id, BoardId = task.BoardId, TenantId = task.TenantId, Title = task.Title };
        var thread = await _runtime.EnsureThreadAsync(throwaway, ct);   // fresh conversation, discarded after
        var prompt = "Summarize the key decisions, current state, and open items of this conversation as a " +
                     "short brief (a few concise bullet points). Do not use tools.\n\nConversation:\n" + transcript;
        var outcome = await _runtime.RunTurnAsync(
            new AgentRunRequest(role, throwaway, thread, prompt, _maxCompletionTokens, "compact", 0),
            new NoopProjectExplorer(), ct);
        return outcome.Status == AgentRunStatus.Completed ? outcome.Content : "";
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, object body, System.Net.HttpStatusCode code)
    {
        var res = req.CreateResponse(code);
        await res.WriteAsJsonAsync(body);
        return res;
    }
}
