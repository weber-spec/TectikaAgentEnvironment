using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class WriteApprovalActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<WriteApprovalActivity> _logger;

    public WriteApprovalActivity(WorkflowCosmosService cosmos, WorkflowEventPublisher events, ILogger<WriteApprovalActivity> logger)
    {
        _cosmos = cosmos;
        _events = events;
        _logger = logger;
    }

    [Function(nameof(WriteApprovalActivity))]
    public async Task<string> Run([ActivityTrigger] WriteApprovalInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;

        var approval = new Approval
        {
            RunId             = input.RunId,
            TaskId            = input.TaskId,
            TenantId          = input.TenantId,
            StepIndex         = input.StepIndex,
            RequestedFrom     = input.Approvers,
            ActionDescription = input.ActionDescription,
            IdentityToBeUsed  = input.IdentityToBeUsed,
            ExpiresAt         = DateTimeOffset.UtcNow.AddHours(48)
        };

        var saved = await _cosmos.CreateApprovalAsync(approval, ct);

        _logger.LogInformation("Approval {ApprovalId} created for run {RunId} step {Step}",
            saved.Id, input.RunId, input.StepIndex);

        // Notify via Service Bus → SSE to frontend
        await _events.PublishApprovalRequiredAsync(input.RunId, input.TaskId, input.StepIndex, saved.Id, ct);

        // TODO Phase 2: send Teams/email notification via Logic App webhook
        // await SendTeamsNotificationAsync(saved, input.Approvers, ct);

        return saved.Id;
    }
}

public record WriteApprovalInput(
    string RunId,
    string TaskId,
    string TenantId,
    int StepIndex,
    List<string> Approvers,
    string ActionDescription,
    string? IdentityToBeUsed = null);
