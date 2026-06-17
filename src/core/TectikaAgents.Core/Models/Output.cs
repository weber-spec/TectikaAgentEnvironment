using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>The kind of product a task output represents. Forward-compatible:
/// only <see cref="Document"/> is produced/rendered in Phase A; the rest are
/// wired up in later specs (Code in Spec 2; Design/Dataset/Deployment/Link beyond).</summary>
public enum OutputKind { Document, Code, Design, Dataset, Deployment, Link }

/// <summary>A product stored directly in Cosmos (small enough to inline).</summary>
public sealed class InlineContent
{
    [JsonPropertyName("contentType")]
    public ArtifactContentType ContentType { get; set; } = ArtifactContentType.Markdown;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>A pointer to a product that lives in an external system of record
/// (git, Canva, a deployment URL, …). <see cref="Locator"/> is provider-specific.</summary>
public sealed class ExternalRef
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("locator")]
    public Dictionary<string, object?> Locator { get; set; } = new();

    [JsonPropertyName("previewUrl")]
    public string? PreviewUrl { get; set; }
}

/// <summary>One deliverable produced by a task. Exactly one of <see cref="Inline"/>
/// or <see cref="External"/> is set.</summary>
public sealed class Output
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("kind")]
    public OutputKind Kind { get; set; } = OutputKind.Document;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("inline")]
    public InlineContent? Inline { get; set; }

    [JsonPropertyName("external")]
    public ExternalRef? External { get; set; }

    /// <summary>True when exactly one of inline / external is populated.</summary>
    public bool IsValid() => (Inline is null) ^ (External is null);
}
