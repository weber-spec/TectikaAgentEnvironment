using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class Approval
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("stepIndex")]
    public int StepIndex { get; set; }

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(48);

    [JsonPropertyName("requestedFrom")]
    public List<string> RequestedFrom { get; set; } = [];

    [JsonPropertyName("status")]
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    [JsonPropertyName("approvedBy")]
    public string? ApprovedBy { get; set; }

    [JsonPropertyName("approvedAt")]
    public DateTimeOffset? ApprovedAt { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("actionDescription")]
    public string ActionDescription { get; set; } = string.Empty;

    [JsonPropertyName("identityToBeUsed")]
    public string? IdentityToBeUsed { get; set; }
}

public enum ApprovalStatus { Pending, Approved, Rejected, Expired }
