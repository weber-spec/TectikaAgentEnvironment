using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>Builds the persisted <see cref="HumanInteraction"/> for a paused steerable run from the
/// round's <see cref="PendingControl"/>. Pure + deterministic (stable id) so the control→type mapping
/// is unit-testable and the upsert is idempotent across Durable activity retries.</summary>
public static class SteerableInteractionFactory
{
    public static HumanInteraction Build(
        string runId, string taskId, string boardId, string tenantId, int round,
        string? humanAuditorId, PendingControl control)
    {
        var interaction = new HumanInteraction
        {
            Id = $"{runId}-r{round}-interaction",
            TenantId = tenantId,
            RunId = runId,
            TaskId = taskId,
            BoardId = boardId,
            StepIndex = round,
            Origin = InteractionOrigin.Steerable,
            Status = InteractionStatus.Pending,
            ActionDescription = control.Text,
            RequestedFrom = string.IsNullOrEmpty(humanAuditorId) ? [] : [humanAuditorId],
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(48),
            Type = control.Kind == PendingControlKind.Approval ? InteractionType.Approval : InteractionType.Question,
        };
        if (interaction.Type == InteractionType.Question)
        {
            interaction.Question = control.Text;
            if (control.Options is { Count: > 0 })
                interaction.QuestionOptions = control.Options.ToList();
        }
        return interaction;
    }
}
