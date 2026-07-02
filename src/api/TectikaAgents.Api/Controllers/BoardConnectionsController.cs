using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

/// <summary>Enable/disable tenant connections on a board (with per-board binding config, e.g. the GitHub repo).
/// The credential lives in the tenant registry (see <see cref="ConnectionsController"/>); this only records which
/// connections a board's agents may use and any board-specific config.</summary>
[ApiController]
[Route("api/boards/{boardId}/connections")]
[Authorize]
public class BoardConnectionsController : ControllerBase
{
    public sealed record BindRequest(Dictionary<string, string>? Config);

    private readonly ICosmosDbService _cosmos;
    public BoardConnectionsController(ICosmosDbService cosmos) => _cosmos = cosmos;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    /// <summary>The board's connection bindings (which tenant connections it enabled + their config).</summary>
    [HttpGet]
    public async Task<IActionResult> List(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        return board is null ? NotFound() : Ok(board.Connections);
    }

    /// <summary>Enable a tenant connection on this board (idempotent) and set/replace its binding config.</summary>
    [HttpPut("{connectionId}")]
    public async Task<IActionResult> Bind(string boardId, string connectionId, [FromBody] BindRequest? req, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();

        // The connection must exist in the tenant registry.
        var conn = await _cosmos.GetConnectionAsync(TenantId, connectionId, ct);
        if (conn is null) return BadRequest(new { error = "UnknownConnection" });

        var binding = board.Connections.FirstOrDefault(b => b.ConnectionId == connectionId);
        if (binding is null)
        {
            binding = new BoardConnectionBinding { ConnectionId = connectionId };
            board.Connections.Add(binding);
        }
        binding.Config = req?.Config ?? new();
        await _cosmos.UpdateBoardAsync(board, ct);
        return Ok(binding);
    }

    /// <summary>Disable a connection on this board (removes the binding; the tenant connection is untouched).</summary>
    [HttpDelete("{connectionId}")]
    public async Task<IActionResult> Unbind(string boardId, string connectionId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var binding = board.Connections.FirstOrDefault(b => b.ConnectionId == connectionId);
        if (binding is null) return NoContent();
        board.Connections.Remove(binding);
        await _cosmos.UpdateBoardAsync(board, ct);
        return NoContent();
    }
}
