using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class UserSettingsDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "preferences";

    public string UserId { get; set; } = string.Empty;

    public NotificationPreferences Notifications { get; set; } = new();

    public DateTimeOffset NotificationsLastReadAt { get; set; } = DateTimeOffset.MinValue;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class NotificationPreferences
{
    // Task updates group
    public bool TaskCompleted { get; set; } = true;
    public bool ApprovalRequired { get; set; } = true;
    public bool TaskFailed { get; set; } = true;
    public bool TaskBlocked { get; set; } = false;
    public bool DependencyResolved { get; set; } = false;

    // Agent updates group
    public bool AgentCreated { get; set; } = true;
    public bool AgentDeleted { get; set; } = true;
}
