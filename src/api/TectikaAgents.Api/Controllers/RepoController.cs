using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/boards/{boardId}/repo")]
[Authorize]
public class RepoController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly IGitHubReadService _gh;
    private readonly IWorkspaceService _workspace;
    private readonly ISecretProvider _secrets;

    public RepoController(ICosmosDbService cosmos, IGitHubReadService gh, IWorkspaceService workspace, ISecretProvider secrets)
    {
        _cosmos = cosmos;
        _gh = gh;
        _workspace = workspace;
        _secrets = secrets;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    private async Task<(GitHubRepoConnection? repo, IActionResult? error)> ResolveAsync(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return (null, NotFound());
        if (board.GitHub is null) return (null, Conflict(new { error = "GitHubNotConnected" }));
        return (board.GitHub, null);
    }

    /// <summary>For a board with NO connected repo: the live workspace (endpoint + token) whose
    /// /workspace/main holds the board's files, or null if the board has a repo or no active workspace.
    /// Lets the Files tab browse the board workspace identically to a GitHub repo.</summary>
    private async Task<(string endpoint, string token)?> ResolveWorkspaceAsync(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null || board.GitHub is not null) return null;            // not a no-repo board
        if (board.WorkspaceStatus != BoardWorkspaceStatus.Ready || string.IsNullOrEmpty(board.WorkspaceEndpoint))
            return null;                                                        // no live workspace to browse
        var token = await _secrets.GetSecretAsync($"workspace-token-board-{boardId}", ct);
        return string.IsNullOrEmpty(token) ? null : (board.WorkspaceEndpoint!, token!);
    }

    /// <summary>Map the executor /list response to the same TreeEntry shape the GitHub path returns.</summary>
    public static List<TreeEntry> ParseWorkspaceTree(string json, string dir)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<TreeEntry>();
        if (doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var e in entries.EnumerateArray())
            {
                var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "file" : "file";
                var size = e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                var full = string.IsNullOrEmpty(dir) ? name : $"{dir}/{name}";
                list.Add(new TreeEntry(name, full, type, size));
            }
        return list;
    }

    /// <summary>Map the executor /read response to the same FileContent shape the GitHub path returns.</summary>
    public static FileContent ParseWorkspaceFile(string json, string path)
    {
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        return new FileContent(path, "", content.Length, false, content);
    }

    [HttpGet("meta")]
    public async Task<IActionResult> Meta(string boardId, CancellationToken ct)
    {
        if (await ResolveWorkspaceAsync(boardId, ct) is not null)
            return Ok(new RepoMeta("main", "Board workspace (no connected repo)", false));
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        return Ok(await _gh.GetRepoMetadataAsync(repo!, ct));
    }

    [HttpGet("branches")]
    public async Task<IActionResult> Branches(string boardId, CancellationToken ct)
    {
        if (await ResolveWorkspaceAsync(boardId, ct) is not null)
            return Ok(new[] { new BranchInfo("main", "") });
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        return Ok(await _gh.ListBranchesAsync(repo!, ct));
    }

    [HttpGet("tree")]
    public async Task<IActionResult> Tree(string boardId, [FromQuery] string? @ref, [FromQuery] string? path, CancellationToken ct)
    {
        if (await ResolveWorkspaceAsync(boardId, ct) is { } ws)
        {
            var listJson = await _workspace.InvokeAsync(ws.endpoint, ws.token, "/list", new { path = path ?? "" }, ct);
            return Ok(ParseWorkspaceTree(listJson, path ?? ""));
        }
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        var r = string.IsNullOrEmpty(@ref) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @ref;
        return Ok(await _gh.ListDirectoryAsync(repo!, r, path ?? "", ct));
    }

    [HttpGet("file")]
    public async Task<IActionResult> File(string boardId, [FromQuery] string? @ref, [FromQuery] string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path)) return BadRequest(new { error = "path is required" });
        if (await ResolveWorkspaceAsync(boardId, ct) is { } ws)
        {
            var fileJson = await _workspace.InvokeAsync(ws.endpoint, ws.token, "/read", new { path }, ct);
            return Ok(ParseWorkspaceFile(fileJson, path));
        }
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
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
