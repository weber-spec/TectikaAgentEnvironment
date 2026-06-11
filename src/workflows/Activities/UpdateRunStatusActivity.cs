using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class UpdateRunStatusActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<UpdateRunStatusActivity> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public UpdateRunStatusActivity(
        WorkflowCosmosService cosmos,
        WorkflowEventPublisher events,
        ILogger<UpdateRunStatusActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration config)
    {
        _cosmos = cosmos;
        _events = events;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    [Function(nameof(UpdateRunStatusActivity))]
    public async Task Run([ActivityTrigger] UpdateRunStatusInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;

        _logger.LogInformation("[UpdateRunStatus] run={RunId} task={TaskId} -> {Status} step={Step}",
            input.RunId, input.TaskId, input.Status, input.CurrentStep);

        var run = await _cosmos.GetRunAsync(input.TaskId, input.RunId, ct);
        if (run is null)
        {
            _logger.LogWarning("[UpdateRunStatus] run {RunId} not found — skipping status update", input.RunId);
            return;
        }

        run.Status = input.Status;
        if (input.CurrentStep.HasValue) run.CurrentStep = input.CurrentStep.Value;

        // Accumulate step results
        if (input.StepResult is not null)
        {
            run.Steps.Add(input.StepResult);
            run.TotalTokens += input.StepResult.TokenUsage.Total;
            run.EstimatedCostUsd = run.TotalTokens * 0.00003m; // ~$0.03/1k tokens gpt-4o
        }

        if (input.Status == RunStatus.Completed || input.Status == RunStatus.Failed)
            run.CompletedAt = DateTimeOffset.UtcNow;

        await _cosmos.UpdateRunAsync(run, ct);

        // Mirror status on the task
        var taskStatus = input.Status switch
        {
            RunStatus.Running            => AgentTaskStatus.InProgress,
            RunStatus.PausedApproval     => AgentTaskStatus.AwaitingApproval,
            RunStatus.AwaitingInteraction => AgentTaskStatus.AwaitingInteraction,
            RunStatus.Completed          => AgentTaskStatus.Done,
            RunStatus.Failed             => AgentTaskStatus.Failed,
            RunStatus.NeedsRevision      => AgentTaskStatus.Review,
            _                            => AgentTaskStatus.InProgress
        };

        await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, taskStatus, input.RunId, ct);

        if (taskStatus is AgentTaskStatus.Done or AgentTaskStatus.Failed)
            await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, "", ct);

        if (input.Status == RunStatus.Completed)
            await _events.PublishRunCompletedAsync(input.RunId, input.TaskId, ct);
        else if (input.Status == RunStatus.Failed)
            await _events.PublishRunFailedAsync(input.RunId, input.TaskId, input.ErrorMessage ?? "Unknown error", ct);

        if (taskStatus == AgentTaskStatus.Done)
        {
            await ResetQaEdgeIterationsAsync(input.BoardId, input.TaskId, ct);
            await TriggerDownstreamRunsAsync(input.BoardId, input.TaskId, ct);
        }
        else if (taskStatus == AgentTaskStatus.Review)
            await TryTriggerQaLoopAsync(input.BoardId, input.TaskId, ct);
    }

    private async Task TryTriggerQaLoopAsync(string boardId, string validatorTaskId, CancellationToken ct)
    {
        List<TaskEdge> feedbackEdges;
        try { feedbackEdges = await _cosmos.GetOutgoingQaFeedbackEdgesAsync(boardId, validatorTaskId, ct); }
        catch (Exception ex) { _logger.LogError(ex, "[QaLoop] QaFeedback edge query failed for {TaskId}", validatorTaskId); return; }

        if (feedbackEdges.Count == 0) return;

        foreach (var edge in feedbackEdges)
            await ProcessQaFeedbackEdgeAsync(boardId, validatorTaskId, edge, ct);
    }

    private async Task ProcessQaFeedbackEdgeAsync(
        string boardId, string validatorTaskId, TaskEdge edge, CancellationToken ct)
    {
        edge.CurrentIterations++;
        await _cosmos.UpdateEdgeAsync(edge, ct);

        _logger.LogInformation("[QaLoop] edge {EdgeId}: iteration {Current}/{Max}",
            edge.Id, edge.CurrentIterations, edge.MaxIterations);

        if (edge.CurrentIterations >= edge.MaxIterations)
        {
            _logger.LogWarning("[QaLoop] exhausted on edge {EdgeId} after {N} iterations — no re-trigger",
                edge.Id, edge.CurrentIterations);
            return;
        }

        var loopTargetId = edge.TargetTaskId;

        List<string> segmentIds;
        try { segmentIds = await _cosmos.GetTasksBetweenAsync(boardId, loopTargetId, validatorTaskId, ct); }
        catch (Exception ex) { _logger.LogError(ex, "[QaLoop] GetTasksBetween failed"); return; }

        await _cosmos.ResetTasksToBacklogAsync(boardId, segmentIds, ct);

        var apiBaseUrl = _config["Api:BaseUrl"];
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            _logger.LogWarning("[QaLoop] Api:BaseUrl not configured — QA retry skipped for {TaskId}", loopTargetId);
            return;
        }

        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new
                { boardId, taskId = loopTargetId, pipeline = (object?)null });
            var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var http = _httpClientFactory.CreateClient();
            var response = await http.PostAsync($"{apiBaseUrl.TrimEnd('/')}/api/runs/start", content, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogError("[QaLoop] retry POST returned {Status} for task {TaskId}",
                    (int)response.StatusCode, loopTargetId);
        }
        catch (Exception ex) { _logger.LogError(ex, "[QaLoop] retry trigger failed for {TaskId}", loopTargetId); }
    }

    private async Task ResetQaEdgeIterationsAsync(string boardId, string taskId, CancellationToken ct)
    {
        try
        {
            var edges = await _cosmos.GetOutgoingQaFeedbackEdgesAsync(boardId, taskId, ct);
            foreach (var edge in edges.Where(e => e.CurrentIterations > 0))
            {
                edge.CurrentIterations = 0;
                await _cosmos.UpdateEdgeAsync(edge, ct);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[QaLoop] failed to reset QA iterations on success for {TaskId}", taskId); }
    }

    private async Task TriggerDownstreamRunsAsync(string boardId, string taskId, CancellationToken ct)
    {
        var apiBaseUrl = _config["Api:BaseUrl"];
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            _logger.LogWarning("[Downstream] Api:BaseUrl is not configured — skipping downstream cascade for task {TaskId}", taskId);
            return;
        }

        List<string> downstreamIds;
        try
        {
            downstreamIds = await _cosmos.GetDownstreamTaskIdsAsync(boardId, taskId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Downstream] failed to get downstream task IDs for task {TaskId}", taskId);
            return;
        }

        foreach (var downstreamId in downstreamIds)
        {
            try
            {
                var downstreamTask = await _cosmos.GetTaskAsync(boardId, downstreamId, ct);
                if (downstreamTask is null)
                {
                    _logger.LogWarning("[Downstream] task {DownstreamId} not found — skipping", downstreamId);
                    continue;
                }

                if (downstreamTask.Status != AgentTaskStatus.Backlog)
                {
                    _logger.LogDebug("[Downstream] task {DownstreamId} is {Status} (not Backlog) — skipping", downstreamId, downstreamTask.Status);
                    continue;
                }

                if (downstreamTask.Assignee.Type != AssigneeType.Agent)
                {
                    _logger.LogDebug("[Downstream] task {DownstreamId} has no agent assignee — skipping", downstreamId);
                    continue;
                }

                // All upstream tasks must be Done
                var upstreamIds = await _cosmos.GetUpstreamTaskIdsAsync(boardId, downstreamId, ct);
                var allUpstreamDone = true;
                foreach (var upstreamId in upstreamIds)
                {
                    var upstreamTask = await _cosmos.GetTaskAsync(boardId, upstreamId, ct);
                    if (upstreamTask is null || upstreamTask.Status != AgentTaskStatus.Done)
                    {
                        allUpstreamDone = false;
                        break;
                    }
                }

                if (!allUpstreamDone)
                {
                    _logger.LogDebug("[Downstream] task {DownstreamId} has unfinished upstream dependencies — skipping", downstreamId);
                    continue;
                }

                // All checks passed — trigger the downstream run
                var body = JsonSerializer.Serialize(new { boardId, taskId = downstreamId, pipeline = (object?)null });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var http = _httpClientFactory.CreateClient();
                var url = $"{apiBaseUrl.TrimEnd('/')}/api/runs/start";

                _logger.LogInformation("[Downstream] triggering downstream run for task {DownstreamId} via {Url}", downstreamId, url);
                var response = await http.PostAsync(url, content, ct);

                if (!response.IsSuccessStatusCode)
                    _logger.LogError("[Downstream] POST {Url} for downstream task {DownstreamId} returned {StatusCode}", url, downstreamId, (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Downstream] error triggering downstream run for task {DownstreamId}", downstreamId);
            }
        }
    }
}

public record UpdateRunStatusInput(
    string RunId,
    string TaskId,
    string BoardId,
    RunStatus Status,
    int? CurrentStep,
    StepResult? StepResult = null,
    string? ErrorMessage = null);
