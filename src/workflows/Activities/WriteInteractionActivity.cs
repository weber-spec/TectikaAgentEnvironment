using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class WriteInteractionActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly WorkflowEventPublisher _events;
    private readonly ILogger<WriteInteractionActivity> _logger;

    public WriteInteractionActivity(WorkflowCosmosService cosmos, WorkflowEventPublisher events, ILogger<WriteInteractionActivity> logger)
    {
        _cosmos = cosmos;
        _events = events;
        _logger = logger;
    }

    [Function(nameof(WriteInteractionActivity))]
    public async Task<string> Run([ActivityTrigger] WriteInteractionInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;

        var interaction = new HumanInteraction
        {
            RunId             = input.RunId,
            TaskId            = input.TaskId,
            BoardId           = input.BoardId,
            TenantId          = input.TenantId,
            StepIndex         = input.StepIndex,
            Type              = input.Pending.Type,
            ActionDescription = input.Pending.ActionDescription,
            RequestedFrom     = input.Approvers,
            Items             = input.Pending.Items,
            Question          = input.Pending.Question,
            QuestionOptions   = input.Pending.QuestionOptions,
            ExpiresAt         = DateTimeOffset.UtcNow.AddHours(48)
        };

        var saved = await _cosmos.CreateInteractionAsync(interaction, ct);

        _logger.LogInformation("Interaction {Id} ({Type}) created for run {RunId} step {Step}",
            saved.Id, saved.Type, input.RunId, input.StepIndex);

        await _events.PublishInteractionRequiredAsync(
            input.RunId, input.TaskId, input.StepIndex, saved.Id, saved.Type.ToString(), ct);

        return saved.Id;
    }
}

public record WriteInteractionInput(
    string RunId,
    string TaskId,
    string BoardId,
    string TenantId,
    int StepIndex,
    List<string> Approvers,
    PendingInteractionRequest Pending);
