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

        logger.LogInformation("[Pipeline] start task={TaskId} run={RunId} steps={Steps}",
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
                logger.LogInformation("[Pipeline] stage={Stage} task={TaskId} step={Step}", "ApprovalGate", input.TaskId, step.Step);

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
                    logger.LogWarning("[Pipeline] rejected task={TaskId} step={Step} approval={ApprovalId}", input.TaskId, step.Step, approvalId);

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

            logger.LogInformation("[Pipeline] stage={Stage} task={TaskId} step={Step} agent={Agent}", "AgentStep", input.TaskId, step.Step, step.AgentRoleId);

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
                logger.LogError(ex, "[Pipeline] step failed task={TaskId} step={Step} agent={Agent}", input.TaskId, step.Step, step.AgentRoleId);

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

            // ── Interaction Gate (agent-requested) ────────────────────────────
            if (stepResult.PendingInteraction is not null)
            {
                logger.LogInformation("[Pipeline] stage={Stage} task={TaskId} step={Step} type={Type}", "AwaitingInteraction", input.TaskId, step.Step, stepResult.PendingInteraction.Type);

                await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                    new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.AwaitingInteraction, step.Step));

                var interactionId = await context.CallActivityAsync<string>(
                    nameof(WriteInteractionActivity),
                    new WriteInteractionInput(
                        input.RunId,
                        input.TaskId,
                        input.BoardId,
                        input.TenantId,
                        step.Step,
                        step.Approvers,
                        stepResult.PendingInteraction));

                var response = await context.WaitForExternalEvent<InteractionResponsePayload>(
                    $"interaction-{step.Step}",
                    TimeSpan.FromHours(48));

                var briefEntry = response.InteractionType switch
                {
                    "Selection" => $"[Human, {response.InteractionId[..Math.Min(6, response.InteractionId.Length)]}, Selection]: Selected \"{response.SelectedTitle}\" — {response.SelectedPrice}",
                    "Question"  => $"[Human, {response.InteractionId[..Math.Min(6, response.InteractionId.Length)]}, Question]: \"{response.Answer}\"",
                    _           => $"[Human, {response.InteractionId[..Math.Min(6, response.InteractionId.Length)]}, Approval]: {(response.Approved == true ? "Approved" : "Rejected")}{(string.IsNullOrEmpty(response.Notes) ? "" : $" — {response.Notes}")}",
                };

                await context.CallActivityAsync(nameof(AppendTaskBriefActivity),
                    new AppendTaskBriefInput(input.BoardId, input.TaskId, briefEntry));

                await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                    new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Running, step.Step));

                // Re-invoke the same agent so it incorporates the human response into a new artifact.
                // Downstream tasks use SELECT TOP 1 ... ORDER BY version DESC, so they will receive
                // this updated artifact instead of the pre-interaction partial response.
                StepResult followUpResult;
                try
                {
                    followUpResult = await context.CallActivityAsync<StepResult>(
                        nameof(InvokeAgentActivity),
                        new InvokeAgentInput(
                            input.RunId,
                            input.TaskId,
                            input.BoardId,
                            input.TenantId,
                            step.AgentRoleId,
                            step.Step + 1));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Pipeline] interaction follow-up step failed task={TaskId} step={Step}", input.TaskId, step.Step + 1);
                    await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                        new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Failed, step.Step + 1, ErrorMessage: ex.Message));
                    return new OrchestrationResult(input.RunId, RunStatus.Failed, completedSteps, ex.Message);
                }

                completedSteps.Add(followUpResult);
                await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
                    new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Running, step.Step + 1, StepResult: followUpResult));

                // If the follow-up also needs interaction, handle it recursively by updating stepResult
                // so the outer loop's NeedsRevision / completion logic applies to the latest result.
                stepResult = followUpResult;
            }
        }

        // All steps done
        await context.CallActivityAsync(nameof(UpdateRunStatusActivity),
            new UpdateRunStatusInput(input.RunId, input.TaskId, input.BoardId, RunStatus.Completed,
                CurrentStep: input.Steps.Count));

        logger.LogInformation("[Pipeline] complete task={TaskId} run={RunId} status={Status} steps={Count}",
            input.TaskId, input.RunId, RunStatus.Completed, completedSteps.Count);
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
