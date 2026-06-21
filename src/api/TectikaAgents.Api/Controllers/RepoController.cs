using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Api.Services;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/boards/{boardId}/repo")]
[Authorize]
public class RepoController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly IGitHubReadService _gh;

    public RepoController(ICosmosDbService cosmos, IGitHubReadService gh)
    {
        _cosmos = cosmos;
        _gh = gh;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    private async Task<(Core.Models.GitHubRepoConnection? repo, IActionResult? error)> ResolveAsync(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return (null, NotFound());
        if (board.GitHub is null) return (null, Conflict(new { error = "GitHubNotConnected" }));
        return (board.GitHub, null);
    }

    [HttpGet("meta")]
    public async Task<IActionResult> Meta(string boardId, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        return Ok(await _gh.GetRepoMetadataAsync(repo!, ct));
    }

    [HttpGet("branches")]
    public async Task<IActionResult> Branches(string boardId, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        return Ok(await _gh.ListBranchesAsync(repo!, ct));
    }

    [HttpGet("tree")]
    public async Task<IActionResult> Tree(string boardId, [FromQuery] string? @ref, [FromQuery] string? path, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        var r = string.IsNullOrEmpty(@ref) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @ref;
        return Ok(await _gh.ListDirectoryAsync(repo!, r, path ?? "", ct));
    }

    [HttpGet("file")]
    public async Task<IActionResult> File(string boardId, [FromQuery] string? @ref, [FromQuery] string path, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        if (string.IsNullOrEmpty(path)) return BadRequest(new { error = "path is required" });
        var r = string.IsNullOrEmpty(@ref) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @ref;
        return Ok(await _gh.GetFileAsync(repo!, r, path, ct));
    }

    [HttpGet("commits")]
    public async Task<IActionResult> Commits(string boardId, [FromQuery] string? @ref, [FromQuery] string? path, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        var r = string.IsNullOrEmpty(@ref) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @ref;
        return Ok(await _gh.ListCommitsAsync(repo!, r, path, Math.Max(1, page), ct));
    }

    /// <summary>List pull requests. state = open (default) | closed | all.</summary>
    [HttpGet("pulls")]
    public async Task<IActionResult> Pulls(string boardId, [FromQuery] string? state, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        return Ok(await _gh.ListPullRequestsAsync(repo!, state ?? "open", ct));
    }

    [HttpGet("pulls/{number:int}")]
    public async Task<IActionResult> Pull(string boardId, int number, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        var pr = await _gh.GetPullRequestAsync(repo!, number, ct);
        return pr is null ? NotFound() : Ok(pr);
    }

    [HttpGet("compare")]
    public async Task<IActionResult> Compare(string boardId, [FromQuery] string? @base, [FromQuery] string head, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        if (string.IsNullOrEmpty(head)) return BadRequest(new { error = "head is required" });
        var b = string.IsNullOrEmpty(@base) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @base;
        return Ok(await _gh.CompareAsync(repo!, b, head, ct));
    }
}
