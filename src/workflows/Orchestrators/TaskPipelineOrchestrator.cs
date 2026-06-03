using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Activities;

namespace TectikaAgents.Workflows.Orchestrators;

/// <summary>
/// Durable Functions orchestrator — מנהל pipeline מקצה לקצה.
///
/// חשוב (Java parallel: כמו Temporal Workflow):
/// - הפונקציה הזו מריצה מחדש מההתחלה בכל checkpoint (replay)
/// - כל side effect (Cosmos, HTTP, ServiceBus) חייב להיות בתוך Activity בלבד
/// - אל תכניס קריאות async שאינן CallActivityAsync / WaitForExternalEvent
/// </summary>
public class TaskPipelineOrchestrator
{
    [Function(nameof(TaskPipelineOrchestrator))]
    public async Task<OrchestrationResult> RunOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<TaskPipelineOrchestrator>();
        var input = context.GetInput<PipelineInput>()!;

        logger.LogInformation("Pipeline started: task={TaskId} run={RunId} steps={Steps}",
            input.TaskId, input.RunId, input.Steps.Count);

        var completedSteps = new List<StepResult>();

        // Mark run as Running
        await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
            new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Running, 0));

        for (int i = 0; i < input.Steps.Count; i++)
        {
            var step = input.Steps[i];

            // ── Approval Gate ────────────────────────────────────────────────
            if (step.Type == StepType.ApprovalGate)
            {
                logger.LogInformation("Approval gate at step {Step}", step.Step);

                // Update run status to PausedApproval
                await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                    new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.PausedApproval, step.Step));

                // Write approval document + notify
                var approvalId = await context.CallActivityAsync<string>(
                    nameof(WriteApprovalActivity),
                    new WriteApprovalInput(
                        input.RunId,
                        input.TaskId,
                        input.TenantId,
                        step.Step,
                        step.Approvers,
                        ActionDescription: $"Step {step.Step} approval required",
                        IdentityToBeUsed: $"role:{step.AgentRoleId}"));

                // Wait up to 48h for human decision
                var decision = await context.WaitForExternalEvent<string>(
                    $"approval-gate-{step.Step}",
                    TimeSpan.FromHours(48));

                if (decision != "Approved")
                {
                    logger.LogWarning("Pipeline rejected at step {Step} (approval {ApprovalId})", step.Step, approvalId);

                    await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                        new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Failed, step.Step,
                            ErrorMessage: "Rejected by approver"));

                    await context.CallActivityAsync(nameof(WriteAuditActivity),
                        new WriteAuditInput(input.TenantId, input.RunId, input.TaskId,
                            step.AgentRoleId ?? "approval-gate", "approval.rejected", AuditOutcome.Denied));

                    return new OrchestrationResult(input.RunId, RunStatus.Failed, completedSteps, "Rejected by approver");
                }

                // Resume after approval
                await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                    new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Running, step.Step));

                continue;
            }

            // ── Agent Execution Step ─────────────────────────────────────────
            if (string.IsNullOrEmpty(step.AgentRoleId)) continue;

            logger.LogInformation("Executing step {Step}: agent={Agent}", step.Step, step.AgentRoleId);

            StepResult stepResult;
            try
            {
                stepResult = await context.CallActivityAsync<StepResult>(
                    nameof(InvokeAgentActivity),
                    new InvokeAgentInput(
                        input.RunId,
                        input.TaskId,
                        input.BoardId,
                        input.TenantId,
                        step.AgentRoleId,
                        step.Step));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Step {Step} failed for agent {Agent}", step.Step, step.AgentRoleId);

                await context.CallActivityAsync(nameof(WriteAuditActivity),
                    new WriteAuditInput(input.TenantId, input.RunId, input.TaskId,
                        step.AgentRoleId, $"step.{step.Step}.failed", AuditOutcome.Failed));

                await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                    new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Failed, step.Step,
                        ErrorMessage: ex.Message));

                return new OrchestrationResult(input.RunId, RunStatus.Failed, completedSteps, ex.Message);
            }

            completedSteps.Add(stepResult);

            // Persist step result in run document
            await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Running, step.Step,
                    StepResult: stepResult));

            await context.CallActivityAsync(nameof(WriteAuditActivity),
                new WriteAuditInput(input.TenantId, input.RunId, input.TaskId,
                    step.AgentRoleId, $"step.{step.Step}.completed", AuditOutcome.Success,
                    TokenUsage: stepResult.TokenUsage, DurationMs: stepResult.DurationMs));
        }

        // All steps done
        await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
            new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Completed,
                CurrentStep: input.Steps.Count));

        logger.LogInformation("Pipeline completed: run={RunId} steps={Count}", input.RunId, completedSteps.Count);
        return new OrchestrationResult(input.RunId, RunStatus.Completed, completedSteps, null);
    }
}

public record PipelineInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    List<PipelineStep> Steps);

public record OrchestrationResult(
    string RunId,
    RunStatus Status,
    List<StepResult> Steps,
    string? Error);
