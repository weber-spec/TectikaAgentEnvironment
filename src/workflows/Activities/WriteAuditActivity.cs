using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Activities;

public class WriteAuditActivity
{
    private readonly ILogger<WriteAuditActivity> _logger;

    public WriteAuditActivity(ILogger<WriteAuditActivity> logger) => _logger = logger;

    [Function(nameof(WriteAuditActivity))]
    public async Task Run(
        [ActivityTrigger] WriteAuditInput input,
        FunctionContext executionContext)
    {
        _logger.LogInformation("Audit: {Action} by agent {AgentRoleId} — {Outcome}",
            input.Action, input.AgentRoleId, input.Outcome);

        // TODO: write to Cosmos auditLog container via CosmosDbService
        await Task.CompletedTask;
    }
}

public record WriteAuditInput(
    string RunId,
    string TaskId,
    string TenantId,
    string AgentRoleId,
    string Action,
    AuditOutcome Outcome);
