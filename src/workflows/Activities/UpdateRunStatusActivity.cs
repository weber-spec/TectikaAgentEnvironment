using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Activities;

public class UpdateRunStatusActivity
{
    private readonly ILogger<UpdateRunStatusActivity> _logger;

    public UpdateRunStatusActivity(ILogger<UpdateRunStatusActivity> logger) => _logger = logger;

    [Function(nameof(UpdateRunStatusActivity))]
    public async Task Run(
        [ActivityTrigger] UpdateRunStatusInput input,
        FunctionContext executionContext)
    {
        _logger.LogInformation("Run {RunId} status → {Status} (step {Step})",
            input.RunId, input.Status, input.CurrentStep);

        // TODO: update WorkflowRun in Cosmos + publish AgentEvent to Service Bus
        await Task.CompletedTask;
    }
}

public record UpdateRunStatusInput(
    string RunId,
    string TaskId,
    RunStatus Status,
    int? CurrentStep);
