using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Activity — טוען AgentRole + upstream artifacts, קורא ל-Azure OpenAI,
/// שומר artifact ב-Cosmos, ומחזיר StepResult.
/// כל side effect מתבצע כאן (לא ב-Orchestrator) למניעת replay בעיות.
/// </summary>
public class InvokeAgentActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly WorkflowAgentRunner _runner;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<InvokeAgentActivity> _logger;

    public InvokeAgentActivity(
        WorkflowCosmosService cosmos,
        WorkflowAgentRunner runner,
        WorkflowEventPublisher events,
        ILogger<InvokeAgentActivity> logger)
    {
        _cosmos = cosmos;
        _runner = runner;
        _events = events;
        _logger = logger;
    }

    [Function(nameof(InvokeAgentActivity))]
    public async Task<StepResult> Run(
        [ActivityTrigger] InvokeAgentInput input,
        FunctionContext executionContext)
    {
        var ct = executionContext.CancellationToken;
        var start = DateTimeOffset.UtcNow;

        _logger.LogInformation("InvokeAgent: role={Role} task={Task} step={Step}",
            input.AgentRoleId, input.TaskId, input.Step);

        // ── 1. Load AgentRole ─────────────────────────────────────────────────
        var role = await _cosmos.GetAgentRoleAsync(input.TenantId, input.AgentRoleId, ct)
            ?? throw new Exception($"AgentRole '{input.AgentRoleId}' not found in tenant '{input.TenantId}'");

        // ── 2. Load task ──────────────────────────────────────────────────────
        var task = await _cosmos.GetTaskAsync(input.BoardId, input.TaskId, ct)
            ?? throw new Exception($"Task '{input.TaskId}' not found in board '{input.BoardId}'");

        // ── 3. Load upstream artifacts ────────────────────────────────────────
        var upstreamTaskIds = await _cosmos.GetUpstreamTaskIdsAsync(task.BoardId, input.TaskId, ct);
        var upstreamArtifacts = await _cosmos.GetUpstreamArtifactsAsync(upstreamTaskIds, ct);

        await _events.PublishStepStartedAsync(input.RunId, input.TaskId, input.Step, role.Id, ct);

        // ── 4. Invoke Azure OpenAI ────────────────────────────────────────────
        AgentInvocationResult result;
        try
        {
            result = await _runner.InvokeAsync(role, task, upstreamArtifacts, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent invocation failed for role {Role}", role.Id);
            throw;
        }

        // ── 5. Determine next artifact version ────────────────────────────────
        var existingArtifacts = await _cosmos.GetUpstreamArtifactsAsync([input.TaskId], ct);

        var nextVersion = (existingArtifacts.MaxBy(a => a.Version)?.Version ?? 0) + 1;

        // ── 6. Save artifact to Cosmos ────────────────────────────────────────
        var artifact = new Artifact
        {
            TaskId      = input.TaskId,
            RunId       = input.RunId,
            TenantId    = input.TenantId,
            Version     = nextVersion,
            ContentType = result.ContentType,
            Content     = result.Content,
            Origin      = ArtifactOrigin.Agent,
            InternalLogs = [$"Agent: {role.DisplayName}", $"Step: {input.Step}", $"Model completion: {result.CompletionId}"],
            InputContext = new ArtifactInputContext
            {
                UpstreamArtifacts = upstreamArtifacts.Select(a => new UpstreamArtifactRef
                {
                    TaskId = a.TaskId,
                    ArtifactId = a.Id,
                    Version = a.Version,
                    ContentType = a.ContentType
                }).ToList()
            }
        };

        var savedArtifact = await _cosmos.CreateArtifactAsync(artifact, ct);

        // Update task's current artifact pointer
        await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, AgentTaskStatus.InProgress, input.RunId, ct);

        var usage = new TokenUsage { Input = result.InputTokens, Output = result.OutputTokens };
        await _events.PublishStepCompletedAsync(input.RunId, input.TaskId, input.Step, role.Id, usage, ct);

        return new StepResult
        {
            Step         = input.Step,
            Status       = RunStatus.Completed,
            FoundryRunId = result.CompletionId,
            ArtifactId   = savedArtifact.Id,
            TokenUsage   = usage,
            DurationMs   = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds,
            CompletedAt  = DateTimeOffset.UtcNow
        };
    }
}

public record InvokeAgentInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    string AgentRoleId,
    int Step);
