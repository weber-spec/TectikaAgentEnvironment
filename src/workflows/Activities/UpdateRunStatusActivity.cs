using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class UpdateRunStatusActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<UpdateRunStatusActivity> _logger;

    public UpdateRunStatusActivity(WorkflowCosmosService cosmos, WorkflowEventPublisher events, ILogger<UpdateRunStatusActivity> logger)
    {
        _cosmos = cosmos;
        _events = events;
        _logger = logger;
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
