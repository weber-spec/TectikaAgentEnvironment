using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public enum EdgeKind { Dependency, QaFeedback }

/// <summary>
/// A typed, persisted edge in a board's task graph — the single source of truth for a
/// connection (topology) and its semantics (kind/label/loop config). Id is "{source}->{target}".
/// </summary>
public class TaskEdge
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty; // "{sourceTaskId}->{targetTaskId}"

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty;

    [JsonPropertyName("sourceTaskId")]
    public string SourceTaskId { get; set; } = string.Empty;

    [JsonPropertyName("targetTaskId")]
    public string TargetTaskId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public EdgeKind Kind { get; set; } = EdgeKind.Dependency;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    [JsonPropertyName("maxIterations")]
    public int MaxIterations { get; set; } = 3;

    [JsonPropertyName("currentIterations")]
    public int CurrentIterations { get; set; } = 0;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string MakeId(string source, string target) => $"{source}->{target}";
}
