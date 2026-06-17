using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/artifacts")]
[Authorize]
public class ArtifactsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;

    public ArtifactsController(ICosmosDbService cosmos) => _cosmos = cosmos;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    /// <summary>All versions of a task's artifact, newest first, normalized to the handoff shape.</summary>
    [HttpGet("{taskId}")]
    public async Task<IActionResult> GetVersions(string taskId, CancellationToken ct)
    {
        var versions = await _cosmos.GetArtifactVersionsAsync(taskId, ct);
        var shaped = versions.Select(a => a.EnsureHandoffShape()).ToList();
        return Ok(shaped);
    }

    /// <summary>
    /// Save a new artifact version (e.g. a human edit from the workspace's
    /// evolving artifact canvas). Versions are immutable and append-only.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateArtifactRequest req, CancellationToken ct)
    {
        var existing = await _cosmos.GetArtifactVersionsAsync(req.TaskId, ct);
        var nextVersion = existing.Any() ? existing.Max(a => a.Version) + 1 : 1;

        var artifact = new Artifact
        {
            TenantId = TenantId,
            TaskId = req.TaskId,
            RunId = req.RunId,
            Version = nextVersion,
            ContentType = req.ContentType,
            Content = req.Content,
            Origin = ArtifactOrigin.HumanEdit,
        };

        var created = await _cosmos.CreateArtifactAsync(artifact, ct);
        return Ok(created);
    }
}

public record CreateArtifactRequest(
    string TaskId,
    string Content,
    ArtifactContentType ContentType,
    string? RunId);
