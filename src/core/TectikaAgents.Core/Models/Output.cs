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
    public Dictionary<string, string> Locator { get; set; } = new();

    [JsonPropertyName("previewUrl")]
    public string? PreviewUrl { get; set; }
}

/// <summary>Where a deliverable file lives, so the UI knows what to browse: the board's connected
/// repo, or the board workspace (no repo). Both resolve against the board main line.</summary>
public enum FileLinkSource { Workspace, Repo }

/// <summary>A pointer to a deliverable FILE the task produced (workspace-relative path, resolved
/// against the board main line). Lets a deliverable record reference the actual files instead of
/// inlining them; the Files tab (S4) renders these clickable via <see cref="PreviewUrl"/>.</summary>
public sealed class FileLink
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public FileLinkSource Source { get; set; } = FileLinkSource.Workspace;

    [JsonPropertyName("previewUrl")]
    public string? PreviewUrl { get; set; }
}

/// <summary>One deliverable produced by a task: an optional inline description and/or a single
/// external pointer (mutually exclusive), plus optional <see cref="Links"/> to the files it produced.</summary>
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

    /// <summary>File links to the deliverable's actual files (S2). Independent of inline/external.</summary>
    [JsonPropertyName("links")]
    public List<FileLink> Links { get; set; } = [];

    /// <summary>Valid when the deliverable carries at least one of inline / external / links, and inline
    /// and external are not both set (a record's primary content is inline OR a single external pointer).</summary>
    public bool IsValid() =>
        (Inline is not null || External is not null || Links.Count > 0)
        && !(Inline is not null && External is not null);
}
