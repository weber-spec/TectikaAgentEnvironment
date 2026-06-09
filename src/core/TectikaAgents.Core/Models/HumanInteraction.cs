using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class HumanInteraction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty;

    [JsonPropertyName("stepIndex")]
    public int StepIndex { get; set; }

    [JsonPropertyName("type")]
    public InteractionType Type { get; set; }

    [JsonPropertyName("status")]
    public InteractionStatus Status { get; set; } = InteractionStatus.Pending;

    [JsonPropertyName("actionDescription")]
    public string ActionDescription { get; set; } = string.Empty;

    [JsonPropertyName("requestedFrom")]
    public List<string> RequestedFrom { get; set; } = [];

    [JsonPropertyName("requestedAt")]
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("respondedBy")]
    public string? RespondedBy { get; set; }

    [JsonPropertyName("respondedAt")]
    public DateTimeOffset? RespondedAt { get; set; }

    // Selection fields
    [JsonPropertyName("items")]
    public List<SearchResultItem>? Items { get; set; }

    [JsonPropertyName("selectedIndex")]
    public int? SelectedIndex { get; set; }

    // Question fields
    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("questionOptions")]
    public List<string>? QuestionOptions { get; set; }

    [JsonPropertyName("answer")]
    public string? Answer { get; set; }

    // Approval fields
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("approved")]
    public bool? Approved { get; set; }

    [JsonPropertyName("identityToBeUsed")]
    public string? IdentityToBeUsed { get; set; }
}

public class SearchResultItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }

    [JsonPropertyName("details")]
    public List<string>? Details { get; set; }

    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

// Embedded in StepResult to signal the orchestrator
public class PendingInteractionRequest
{
    [JsonPropertyName("type")]
    public InteractionType Type { get; set; }

    [JsonPropertyName("actionDescription")]
    public string ActionDescription { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<SearchResultItem>? Items { get; set; }

    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("questionOptions")]
    public List<string>? QuestionOptions { get; set; }
}

// Payload raised as Durable Functions external event
public record InteractionResponsePayload(
    [property: JsonPropertyName("interactionId")] string InteractionId,
    [property: JsonPropertyName("interactionType")] string InteractionType,
    [property: JsonPropertyName("selectedIndex")] int? SelectedIndex,
    [property: JsonPropertyName("selectedTitle")] string? SelectedTitle,
    [property: JsonPropertyName("selectedPrice")] string? SelectedPrice,
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("approved")] bool? Approved,
    [property: JsonPropertyName("notes")] string? Notes);

public enum InteractionType { Approval, Selection, Question }
public enum InteractionStatus { Pending, Responded, Expired }
