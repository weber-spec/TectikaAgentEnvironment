using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public static class NotificationMapper
{
    public static NotificationDocument? Map(AgentEvent e, string tenantId) => e.Type switch
    {
        AgentEvent.Types.RunCompleted => new NotificationDocument
        {
            TenantId = tenantId,
            Type = "completed",
            Title = $"Task run completed",
            Subtitle = e.AgentRole is not null ? $"Agent: {e.AgentRole}" : null,
            RunId = e.RunId,
            TaskId = e.TaskId,
            SourceEventType = e.Type,
        },
        AgentEvent.Types.RunFailed => new NotificationDocument
        {
            TenantId = tenantId,
            Type = "failed",
            // Content is now the short, user-facing failure message (with its correlation ref) — show it
            // directly rather than prefixing "Error:" in front of a raw exception.
            Title = $"Task run failed",
            Subtitle = e.Content is not null ? Truncate(e.Content, 120) : null,
            RunId = e.RunId,
            TaskId = e.TaskId,
            SourceEventType = e.Type,
        },
        AgentEvent.Types.InteractionRequired => new NotificationDocument
        {
            TenantId = tenantId,
            Type = "approval",
            Title = "Input required",
            Subtitle = e.AgentRole is not null ? $"Agent: {e.AgentRole}" : null,
            RunId = e.RunId,
            TaskId = e.TaskId,
            SourceEventType = e.Type,
        },
        _ => null
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
