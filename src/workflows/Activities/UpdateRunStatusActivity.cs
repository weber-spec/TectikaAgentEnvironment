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

        var run = await _cosmos.GetRunAsync(input.TaskId, input.RunId, ct);
        if (run is null)
        {
            _logger.LogWarning("Run {RunId} not found — skipping status update", input.RunId);
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
            RunStatus.Running         => AgentTaskStatus.InProgress,
            RunStatus.PausedApproval  => AgentTaskStatus.AwaitingApproval,
            RunStatus.Completed       => AgentTaskStatus.Done,
            RunStatus.Failed          => AgentTaskStatus.Failed,
            _                         => AgentTaskStatus.InProgress
        };

        await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, taskStatus, input.RunId, ct);

        if (taskStatus is AgentTaskStatus.Done or AgentTaskStatus.Failed)
            await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, "", ct);

        if (input.Status == RunStatus.Completed)
            await _events.PublishRunCompletedAsync(input.RunId, input.TaskId, ct);
        else if (input.Status == RunStatus.Failed)
            await _events.PublishRunFailedAsync(input.RunId, input.TaskId, input.ErrorMessage ?? "Unknown error", ct);

        if (taskStatus == AgentTaskStatus.Done)
            await TriggerDownstreamRunsAsync(input.BoardId, input.TaskId, ct);
    }

    private async Task TriggerDownstreamRunsAsync(string boardId, string taskId, CancellationToken ct)
    {
        var apiBaseUrl = _config["Api:BaseUrl"];
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            _logger.LogWarning("Api:BaseUrl is not configured — skipping downstream cascade for task {TaskId}", taskId);
            return;
        }

        List<string> downstreamIds;
        try
        {
            downstreamIds = await _cosmos.GetDownstreamTaskIdsAsync(boardId, taskId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get downstream task IDs for task {TaskId}", taskId);
            return;
        }

        foreach (var downstreamId in downstreamIds)
        {
            try
            {
                var downstreamTask = await _cosmos.GetTaskAsync(boardId, downstreamId, ct);
                if (downstreamTask is null)
                {
                    _logger.LogWarning("Downstream task {DownstreamId} not found — skipping", downstreamId);
                    continue;
                }

                if (downstreamTask.Status != AgentTaskStatus.Backlog)
                {
                    _logger.LogDebug("Downstream task {DownstreamId} is {Status} (not Backlog) — skipping", downstreamId, downstreamTask.Status);
                    continue;
                }

                if (downstreamTask.Assignee.Type != AssigneeType.Agent)
                {
                    _logger.LogDebug("Downstream task {DownstreamId} has no agent assignee — skipping", downstreamId);
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
                    _logger.LogDebug("Downstream task {DownstreamId} has unfinished upstream dependencies — skipping", downstreamId);
                    continue;
                }

                // All checks passed — trigger the downstream run
                var body = JsonSerializer.Serialize(new { boardId, taskId = downstreamId, pipeline = (object?)null });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var http = _httpClientFactory.CreateClient();
                var url = $"{apiBaseUrl.TrimEnd('/')}/api/runs/start";

                _logger.LogInformation("Triggering downstream run for task {DownstreamId} via {Url}", downstreamId, url);
                var response = await http.PostAsync(url, content, ct);

                if (!response.IsSuccessStatusCode)
                    _logger.LogError("POST {Url} for downstream task {DownstreamId} returned {StatusCode}", url, downstreamId, (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error triggering downstream run for task {DownstreamId}", downstreamId);
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
