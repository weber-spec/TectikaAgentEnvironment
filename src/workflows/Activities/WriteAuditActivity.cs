using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class WriteAuditActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly ILogger<WriteAuditActivity> _logger;

    public WriteAuditActivity(WorkflowCosmosService cosmos, ILogger<WriteAuditActivity> logger)
    {
        _cosmos = cosmos;
        _logger = logger;
    }

    [Function(nameof(WriteAuditActivity))]
    public async Task Run([ActivityTrigger] WriteAuditInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;

        _logger.LogInformation("[WriteAudit] task={TaskId} event={Event} agent={Agent}",
            input.TaskId, input.Action, input.AgentRoleId);

        var entry = new AuditEntry
        {
            TenantId     = input.TenantId,
            RunId        = input.RunId,
            TaskId       = input.TaskId,
            ActorType    = ActorType.Agent,
            ActorId      = input.AgentRoleId,
            AgentRoleId  = input.AgentRoleId,
            Action       = input.Action,
            IdentityUsed = $"role:{input.AgentRoleId}",
            Outcome      = input.Outcome,
            TokenUsage   = input.TokenUsage,
            DurationMs   = input.DurationMs
        };

        await _cosmos.AppendAuditAsync(entry, ct);

        _logger.LogInformation("[WriteAudit] persisted task={TaskId} event={Event} agent={Agent} outcome={Outcome} tokens={Tokens}",
            input.TaskId, input.Action, input.AgentRoleId, input.Outcome, input.TokenUsage?.Total ?? 0);
    }
}

public record WriteAuditInput(
    string TenantId,
    string RunId,
    string TaskId,
    string AgentRoleId,
    string Action,
    AuditOutcome Outcome,
    TokenUsage? TokenUsage = null,
    long DurationMs = 0);
