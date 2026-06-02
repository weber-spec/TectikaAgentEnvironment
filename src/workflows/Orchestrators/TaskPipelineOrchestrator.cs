using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Activities;

namespace TectikaAgents.Workflows.Orchestrators;

/// <summary>
/// Durable Functions orchestrator — מנהל את הpipeline מקצה לקצה.
///
/// Java parallel: כמו Workflow Engine ב-Temporal/Cadence אבל managed ע"י Azure.
/// הפונקציה מריצה מחדש מתחילה בכל checkpoint (replay) — אל תכניס side effects כאן ישירות.
/// </summary>
public class TaskPipelineOrchestrator
{
    [Function(nameof(TaskPipelineOrchestrator))]
    public async Task<OrchestrationResult> RunOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<TaskPipelineOrchestrator>();
        var input = context.GetInput<PipelineInput>()!;

        logger.LogInformation("Pipeline started for task {TaskId}, run {RunId}", input.TaskId, input.RunId);

        var results = new List<StepResult>();

        for (int i = 0; i < input.Steps.Count; i++)
        {
            var step = input.Steps[i];

            // ── Approval Gate ───────────────────────────────────────────────
            if (step.Type == StepType.ApprovalGate)
            {
                logger.LogInformation("Waiting for approval at step {Step}", step.Step);

                await context.CallActivityAsync(
                    nameof(WriteApprovalActivity),
                    new WriteApprovalInput(input.RunId, input.TaskId, input.TenantId, step.Step, step.Approvers));

                // מחכה לאישור אנושי — יכול לחכות ימים
                var approval = await context.WaitForExternalEvent<string>(
                    $"approval-gate-{step.Step}",
                    TimeSpan.FromHours(48));

                if (approval != "Approved")
                {
                    logger.LogWarning("Pipeline rejected at step {Step}", step.Step);
                    return new OrchestrationResult(input.RunId, RunStatus.Failed, results, "Rejected by approver");
                }

                continue;
            }

            // ── Agent Execution ─────────────────────────────────────────────
            if (string.IsNullOrEmpty(step.AgentRoleId)) continue;

            await context.CallActivityAsync(
                nameof(UpdateRunStatusActivity),
                new UpdateRunStatusInput(input.RunId, input.TaskId, RunStatus.Running, step.Step));

            StepResult stepResult;
            try
            {
                stepResult = await context.CallActivityAsync<StepResult>(
                    nameof(InvokeAgentActivity),
                    new InvokeAgentInput(input.RunId, input.TaskId, input.TenantId, step.AgentRoleId, step.Step));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent step {Step} failed for run {RunId}", step.Step, input.RunId);

                await context.CallActivityAsync(
                    nameof(WriteAuditActivity),
                    new WriteAuditInput(input.RunId, input.TaskId, input.TenantId,
                        step.AgentRoleId, $"step.{step.Step}.failed", AuditOutcome.Failed));

                return new OrchestrationResult(input.RunId, RunStatus.Failed, results, ex.Message);
            }

            results.Add(stepResult);

            await context.CallActivityAsync(
                nameof(WriteAuditActivity),
                new WriteAuditInput(input.RunId, input.TaskId, input.TenantId,
                    step.AgentRoleId, $"step.{step.Step}.completed", AuditOutcome.Success));
        }

        await context.CallActivityAsync(
            nameof(UpdateRunStatusActivity),
            new UpdateRunStatusInput(input.RunId, input.TaskId, RunStatus.Completed, null));

        logger.LogInformation("Pipeline completed for run {RunId}", input.RunId);
        return new OrchestrationResult(input.RunId, RunStatus.Completed, results, null);
    }
}

// ── Input / Output records ────────────────────────────────────────────────────

public record PipelineInput(
    string RunId,
    string TaskId,
    string TenantId,
    List<PipelineStep> Steps);

public record OrchestrationResult(
    string RunId,
    RunStatus Status,
    List<StepResult> Steps,
    string? Error);
