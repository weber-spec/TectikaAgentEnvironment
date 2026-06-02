using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Activity — קורא ל-Foundry Agent Service ומחזיר StepResult.
/// כל side effect (Cosmos write, Service Bus publish) קורה כאן, לא ב-Orchestrator.
/// </summary>
public class InvokeAgentActivity
{
    private readonly ILogger<InvokeAgentActivity> _logger;

    public InvokeAgentActivity(ILogger<InvokeAgentActivity> logger) => _logger = logger;

    [Function(nameof(InvokeAgentActivity))]
    public async Task<StepResult> Run(
        [ActivityTrigger] InvokeAgentInput input,
        FunctionContext executionContext)
    {
        var start = DateTimeOffset.UtcNow;
        _logger.LogInformation("Invoking agent {AgentRoleId} for task {TaskId}, step {Step}",
            input.AgentRoleId, input.TaskId, input.Step);

        // TODO Week 3-4: קריאה ל-Foundry Agent Service
        // 1. Load AgentRole from Cosmos
        // 2. Load upstream artifacts for InputContext
        // 3. Call FoundryAgentService.RunAgentAsync(...)
        // 4. Save artifact to Cosmos
        // 5. Publish AgentEvent to Service Bus (artifact_updated, step_completed)

        await Task.Delay(100); // placeholder

        return new StepResult
        {
            Step = input.Step,
            Status = RunStatus.Completed,
            FoundryRunId = $"foundry-placeholder-{Guid.NewGuid()}",
            TokenUsage = new TokenUsage { Input = 0, Output = 0 },
            DurationMs = (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}

public record InvokeAgentInput(
    string RunId,
    string TaskId,
    string TenantId,
    string AgentRoleId,
    int Step);
