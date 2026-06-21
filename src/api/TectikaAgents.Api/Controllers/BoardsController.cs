using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BoardsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<BoardsController> _logger;

    public BoardsController(ICosmosDbService cosmos, ISecretProvider secrets, ILogger<BoardsController> logger)
    {
        _cosmos = cosmos;
        _secrets = secrets;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        Ok(await _cosmos.GetBoardsAsync(TenantId, ct));

    [HttpGet("{boardId}")]
    public async Task<IActionResult> Get(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        return board is null ? NotFound() : Ok(board);
    }

    [HttpPut("{boardId}")]
    public async Task<IActionResult> Update(string boardId, [FromBody] UpdateBoardRequest req, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var currentUser = User.FindFirst("preferred_username")?.Value;
        if (board.OwnerId != currentUser) return Forbid();
        board.Name = req.Name;
        board.Description = req.Description ?? board.Description;
        return Ok(await _cosmos.UpdateBoardAsync(board, ct));
    }

    [HttpDelete("{boardId}")]
    public async Task<IActionResult> Delete(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var currentUser = User.FindFirst("preferred_username")?.Value;
        if (board.OwnerId != currentUser) return Forbid();
        await _cosmos.DeleteBoardAsync(TenantId, boardId, ct);
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBoardRequest req, CancellationToken ct)
    {
        var board = new Board
        {
            TenantId = TenantId,
            Name = req.Name,
            Description = req.Description ?? string.Empty,
            OwnerId = User.FindFirst("preferred_username")?.Value ?? "unknown"
        };

        var created = await _cosmos.CreateBoardAsync(board, ct);
        return CreatedAtAction(nameof(Get), new { boardId = created.Id }, created);
    }
    [HttpPut("{boardId}/github")]
    public async Task<IActionResult> ConnectGitHub(string boardId,
        [FromBody] ConnectGitHubRequest req, CancellationToken ct)
    {
        // NB: never log req.Pat — it is a GitHub personal access token.
        _logger.LogInformation("[GitHubConnect] board {BoardId} repo {RepoUrl} by {UserId}", boardId, req.RepoUrl, UserId);

        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null)
        {
            _logger.LogWarning("[GitHubConnect] board {BoardId} not found", boardId);
            return NotFound();
        }

        // Parse owner/repo from URL (https://github.com/owner/repo)
        if (!Uri.TryCreate(req.RepoUrl?.TrimEnd('/'), UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("[GitHubConnect] board {BoardId} got malformed RepoUrl {RepoUrl}", boardId, req.RepoUrl);
            return BadRequest("RepoUrl must be an absolute URL in the form https://github.com/owner/repo");
        }
        var parts = uri.AbsolutePath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            _logger.LogWarning("[GitHubConnect] board {BoardId} RepoUrl {RepoUrl} missing owner/repo", boardId, req.RepoUrl);
            return BadRequest("RepoUrl must be in the form https://github.com/owner/repo");
        }

        var secretName = $"github-pat-board-{boardId}";
        if (!string.IsNullOrEmpty(req.Pat))
        {
            try
            {
                await _secrets.SetSecretAsync(secretName, req.Pat, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GitHubConnect] failed to store PAT secret {SecretName} for board {BoardId}", secretName, boardId);
                throw;
            }
        }

        board.GitHub = new GitHubRepoConnection
        {
            RepoUrl = req.RepoUrl,
            Owner = parts[0],
            Repo = GitHubRepoConnection.NormalizeRepoName(parts[1]),
            PatSecretName = secretName
        };

        try
        {
            var updated = await _cosmos.UpdateBoardAsync(board, ct);
            _logger.LogInformation("[GitHubConnect] board {BoardId} connected to {Owner}/{Repo}", boardId, parts[0], parts[1]);
            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitHubConnect] failed to persist board {BoardId} after storing PAT secret {SecretName}", boardId, secretName);
            throw;
        }
    }

    [HttpDelete("{boardId}/github")]
    public async Task<IActionResult> DisconnectGitHub(string boardId, CancellationToken ct)
    {
        _logger.LogInformation("[GitHubDisconnect] board {BoardId} by {UserId}", boardId, UserId);

        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null)
        {
            _logger.LogWarning("[GitHubDisconnect] board {BoardId} not found", boardId);
            return NotFound();
        }
        board.GitHub = null;

        try
        {
            var updated = await _cosmos.UpdateBoardAsync(board, ct);
            _logger.LogInformation("[GitHubDisconnect] board {BoardId} disconnected", boardId);
            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitHubDisconnect] failed to persist board {BoardId}", boardId);
            throw;
        }
    }
}

public record CreateBoardRequest(string Name, string? Description);
public record UpdateBoardRequest(string Name, string? Description);
public record ConnectGitHubRequest(string RepoUrl, string Pat);
