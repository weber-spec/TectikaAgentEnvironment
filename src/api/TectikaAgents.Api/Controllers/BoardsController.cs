using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BoardsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;

    public BoardsController(ICosmosDbService cosmos) => _cosmos = cosmos;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

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
}

public record CreateBoardRequest(string Name, string? Description);
public record UpdateBoardRequest(string Name, string? Description);
