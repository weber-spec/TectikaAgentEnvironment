using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace TectikaAgents.Workflows.Activities;

public class WriteApprovalActivity
{
    private readonly ILogger<WriteApprovalActivity> _logger;

    public WriteApprovalActivity(ILogger<WriteApprovalActivity> logger) => _logger = logger;

    [Function(nameof(WriteApprovalActivity))]
    public async Task Run(
        [ActivityTrigger] WriteApprovalInput input,
        FunctionContext executionContext)
    {
        _logger.LogInformation("Approval gate required at step {Step} for run {RunId}", input.StepIndex, input.RunId);

        // TODO:
        // 1. Write Approval document to Cosmos
        // 2. Send notification via Service Bus → Teams webhook / email
        await Task.CompletedTask;
    }
}

public record WriteApprovalInput(
    string RunId,
    string TaskId,
    string TenantId,
    int StepIndex,
    List<string> Approvers);
