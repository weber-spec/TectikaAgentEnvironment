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
    private readonly IWorkspaceService _workspaceService;
    private readonly IBoardMaintenanceService _maintenance;
    private readonly IWorkspaceControlService _workspaceControl;
    private readonly IChannelProvisioningService _channels;
    private readonly ILogger<BoardsController> _logger;

    public BoardsController(ICosmosDbService cosmos, ISecretProvider secrets, IWorkspaceService workspaceService,
        IBoardMaintenanceService maintenance, IWorkspaceControlService workspaceControl,
        IChannelProvisioningService channels, ILogger<BoardsController> logger)
    {
        _cosmos = cosmos;
        _secrets = secrets;
        _workspaceService = workspaceService;
        _maintenance = maintenance;
        _workspaceControl = workspaceControl;
        _channels = channels;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        // Hidden boards (auto-created to host channel agent chat) never appear in the Boards list.
        Ok((await _cosmos.GetBoardsAsync(TenantId, ct)).Where(b => !b.Hidden));

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
        // Destroy the board's ACI container if one was provisioned.
        if (!string.IsNullOrEmpty(board.WorkspaceContainerName))
        {
            _logger.LogInformation("[BoardsController] destroying workspace container {Container} for board {BoardId}", board.WorkspaceContainerName, boardId);
            await _workspaceService.DestroyBoardContainerAsync(board.WorkspaceContainerName, ct);
        }
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
        await _channels.EnsureBoardChannelAsync(created, ct);   // auto-create the board's channel
        return CreatedAtAction(nameof(Get), new { boardId = created.Id }, created);
    }
    [HttpPut("{boardId}/github")]
    public async Task<IActionResult> ConnectGitHub(string boardId,
        [FromBody] ConnectGitHubRequest req, CancellationToken ct)
    {
        _logger.LogInformation("[GitHubConnect] board {BoardId} repo {RepoUrl} conn {ConnId} by {UserId}", boardId, req.RepoUrl, req.ConnectionId, UserId);

        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null)
        {
            _logger.LogWarning("[GitHubConnect] board {BoardId} not found", boardId);
            return NotFound();
        }

        // The PAT lives in the tenant GitHub connection; the board references its Key Vault secret.
        var connection = await _cosmos.GetConnectionAsync(TenantId, req.ConnectionId, ct);
        if (connection is null || connection.CatalogId != "github")
            return BadRequest(new { error = "UnknownConnection", detail = "Select a GitHub connection from the Connections page." });

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
        var normalizedRepo = BoardGitHubRules.NormalizeRepo(parts[1]);

        // One repo ⇄ one board (tenant-wide). Reject a repo already connected to another board.
        var allBoards = await _cosmos.GetBoardsAsync(TenantId, ct);
        var conflict = BoardGitHubRules.FindConflict(allBoards, boardId, parts[0], normalizedRepo);
        if (conflict is not null)
        {
            _logger.LogWarning("[GitHubConnect] board {BoardId} repo {Owner}/{Repo} already on board {OtherId}", boardId, parts[0], normalizedRepo, conflict.Id);
            return Conflict(new { error = $"Repository {parts[0]}/{normalizedRepo} is already connected to board \"{conflict.Name}\". A repository can be connected to only one board." });
        }

        // Reference the connection's secret directly — no board-scoped PAT copy.
        board.GitHub = new GitHubRepoConnection
        {
            RepoUrl = req.RepoUrl,
            Owner = parts[0],
            Repo = normalizedRepo,
            PatSecretName = connection.SecretName,
        };
        // Record the binding on the board's connection list too (so it appears enabled with its repo config).
        var binding = board.Connections.FirstOrDefault(b => b.ConnectionId == connection.Id);
        if (binding is null)
        {
            binding = new BoardConnectionBinding { ConnectionId = connection.Id };
            board.Connections.Add(binding);
        }
        binding.Config = new() { ["owner"] = parts[0], ["repo"] = normalizedRepo, ["repoUrl"] = req.RepoUrl };

        try
        {
            var updated = await _cosmos.UpdateBoardAsync(board, ct);
            _logger.LogInformation("[GitHubConnect] board {BoardId} connected to {Owner}/{Repo}", boardId, parts[0], normalizedRepo);
            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitHubConnect] failed to persist board {BoardId} (conn {ConnId})", boardId, req.ConnectionId);
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
        // Clear the repo binding; the tenant connection + its shared PAT secret are left intact.
        if (board.GitHub is not null)
            board.Connections.RemoveAll(b => b.Config.GetValueOrDefault("repoUrl") == board.GitHub!.RepoUrl);
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

    // ── Workspace (ACI) control ──────────────────────────────────────────────
    [HttpGet("{boardId}/workspace")]
    public async Task<IActionResult> GetWorkspace(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        return Ok(await _workspaceControl.GetStatusAsync(board, ct));
    }

    [HttpPost("{boardId}/workspace")]
    public async Task<IActionResult> StartWorkspace(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        if (board.OwnerId != UserId) return Forbid();
        return Ok(await _workspaceControl.StartAsync(board, ct));
    }

    [HttpPost("{boardId}/workspace/restart")]
    public async Task<IActionResult> RestartWorkspace(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        if (board.OwnerId != UserId) return Forbid();
        var dto = await _workspaceControl.RestartAsync(board, ct);
        return dto is null
            ? Conflict(new { error = "Cannot restart while the board has active runs. Stop them first." })
            : Ok(dto);
    }

    [HttpDelete("{boardId}/workspace")]
    public async Task<IActionResult> TerminateWorkspace(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        if (board.OwnerId != UserId) return Forbid();
        var ok = await _workspaceControl.TerminateAsync(board, ct);
        return ok
            ? Ok(await _workspaceControl.GetStatusAsync(board, ct))
            : Conflict(new { error = "Cannot terminate while the board has active runs. Stop them first." });
    }

    // ── Reset (destructive) ──────────────────────────────────────────────────
    [HttpPost("{boardId}/reset")]
    public async Task<IActionResult> Reset(string boardId, [FromBody] ResetBoardRequest req, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        if (board.OwnerId != UserId) return Forbid();
        var result = await _maintenance.ResetBoardAsync(board, req.ClearRepo, ct);
        return Ok(result);
    }

    // ── Clone ────────────────────────────────────────────────────────────────
    [HttpPost("{boardId}/clone")]
    public async Task<IActionResult> Clone(string boardId, [FromBody] CloneBoardRequest req, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var clone = await _maintenance.CloneBoardAsync(board, req.Name, req.IncludeData, UserId, ct);
        return CreatedAtAction(nameof(Get), new { boardId = clone.Id }, clone);
    }
}

public record CreateBoardRequest(string Name, string? Description);
public record UpdateBoardRequest(string Name, string? Description);
/// <summary>Connect a repo to a board by selecting a tenant GitHub connection (holds the PAT) + the repo URL.
/// The board references the connection's Key Vault secret rather than storing its own PAT.</summary>
public record ConnectGitHubRequest(string ConnectionId, string RepoUrl);
public record ResetBoardRequest(bool ClearRepo);
public record CloneBoardRequest(string? Name, bool IncludeData);
