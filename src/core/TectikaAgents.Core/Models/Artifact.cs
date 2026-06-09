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

    [JsonPropertyName("origin")]
    public ArtifactOrigin Origin { get; set; } = ArtifactOrigin.Agent;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
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
