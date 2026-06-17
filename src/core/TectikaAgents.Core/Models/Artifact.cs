using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public class Artifact
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("contentType")]
    public ArtifactContentType ContentType { get; set; } = ArtifactContentType.Markdown;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("inputContext")]
    public ArtifactInputContext InputContext { get; set; } = new();

    [JsonPropertyName("internalLogs")]
    public List<string> InternalLogs { get; set; } = [];

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("outputs")]
    public List<Output> Outputs { get; set; } = [];

    [JsonPropertyName("origin")]
    public ArtifactOrigin Origin { get; set; } = ArtifactOrigin.Agent;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Non-destructive read-time normalizer: any legacy artifact (populated
    /// <see cref="Content"/>, empty <see cref="Outputs"/>) is presented as a single
    /// inline Document output, and a missing <see cref="Summary"/> is derived from the
    /// content. New artifacts (with outputs) are returned unchanged.</summary>
    public Artifact EnsureHandoffShape()
    {
        if (Outputs.Count == 0 && !string.IsNullOrEmpty(Content))
        {
            Outputs = [new Output
            {
                Kind = OutputKind.Document,
                Inline = new InlineContent { ContentType = ContentType, Content = Content },
            }];
        }

        if (string.IsNullOrWhiteSpace(Summary))
            Summary = DeriveSummary(Content);

        return this;
    }

    /// <summary>First meaningful line of markdown content, stripped of leading
    /// heading hashes / list markers and truncated, for use as a fallback summary.</summary>
    internal static string DeriveSummary(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var line = content
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
        line = line.TrimStart('#', '-', '*', '>', ' ').Trim();
        return line.Length > 200 ? line[..200].TrimEnd() + "…" : line;
    }
}

public class ArtifactInputContext
{
    [JsonPropertyName("upstreamArtifacts")]
    public List<UpstreamArtifactRef> UpstreamArtifacts { get; set; } = [];

    [JsonPropertyName("humanContext")]
    public string? HumanContext { get; set; }
}

public class UpstreamArtifactRef
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("artifactId")]
    public string ArtifactId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("contentType")]
    public ArtifactContentType ContentType { get; set; }
}

public enum ArtifactContentType { Code, Markdown, Json, Data }
public enum ArtifactOrigin { Agent, HumanEdit, CliBridge }
