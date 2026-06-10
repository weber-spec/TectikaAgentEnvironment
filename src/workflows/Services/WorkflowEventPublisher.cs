using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>
/// מפרסם AgentEvents ל-Service Bus → SSE ל-frontend.
/// </summary>
public class WorkflowEventPublisher
{
    private readonly ServiceBusSender? _sender;
    private readonly ILogger<WorkflowEventPublisher> _logger;

    public WorkflowEventPublisher(ServiceBusClient? sbClient, IOptions<ServiceBusSettings> settings, ILogger<WorkflowEventPublisher> logger)
    {
        _logger = logger;
        _sender = sbClient?.CreateSender(settings.Value.AgentEventsTopic);
    }

    public async Task PublishAsync(AgentEvent agentEvent, CancellationToken ct = default)
    {
        if (_sender is null)
        {
            _logger.LogDebug("[WorkflowEvent] dev — skipped publish {EventType} for run {RunId}", agentEvent.Type, agentEvent.RunId);
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(agentEvent);
            await _sender.SendMessageAsync(new ServiceBusMessage(json), ct);
            _logger.LogDebug("[WorkflowEvent] published {EventType} for run {RunId}", agentEvent.Type, agentEvent.RunId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WorkflowEvent] failed to publish {EventType} for run {RunId}", agentEvent.Type, agentEvent.RunId);
        }
    }

    public Task PublishRunStartedAsync(string runId, string taskId, int totalSteps, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent { Type = AgentEvent.Types.RunStarted, RunId = runId, TaskId = taskId, Content = totalSteps.ToString() }, ct);

    public Task PublishStepStartedAsync(string runId, string taskId, int step, string agentRole, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent { Type = AgentEvent.Types.StepStarted, RunId = runId, TaskId = taskId, Step = step, AgentRole = agentRole }, ct);

    public Task PublishAgentThinkingAsync(string runId, string taskId, int step, string text, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent { Type = AgentEvent.Types.AgentThinking, RunId = runId, TaskId = taskId, Step = step, Content = text }, ct);

    public Task PublishStepCompletedAsync(string runId, string taskId, int step, string agentRole, TokenUsage usage, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent { Type = AgentEvent.Types.StepCompleted, RunId = runId, TaskId = taskId, Step = step, AgentRole = agentRole, TokenUsage = usage }, ct);

    public Task PublishApprovalRequiredAsync(string runId, string taskId, int step, string approvalId, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent { Type = AgentEvent.Types.ApprovalRequired, RunId = runId, TaskId = taskId, Step = step, ApprovalId = approvalId }, ct);

    public Task PublishInteractionRequiredAsync(string runId, string taskId, int step, string interactionId, string interactionType, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent
        {
            Type = AgentEvent.Types.InteractionRequired,
            RunId = runId,
            TaskId = taskId,
            Step = step,
            InteractionId = interactionId,
            InteractionType = interactionType
        }, ct);

    public Task PublishRunCompletedAsync(string runId, string taskId, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent { Type = AgentEvent.Types.RunCompleted, RunId = runId, TaskId = taskId }, ct);

    public Task PublishRunFailedAsync(string runId, string taskId, string error, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent { Type = AgentEvent.Types.RunFailed, RunId = runId, TaskId = taskId, Content = error }, ct);
}
