using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/boards/{boardId}/mcp")]
[Authorize]
public class BoardMcpConnectionsController : ControllerBase
{
    public sealed record ConnectRequest(string CatalogId, string? DisplayName, string Token);

    private readonly ICosmosDbService _cosmos;
    private readonly IMcpGateway _gateway;
    private readonly ISecretProvider _secrets;

    public BoardMcpConnectionsController(ICosmosDbService cosmos, IMcpGateway gateway, ISecretProvider secrets)
    {
        _cosmos = cosmos;
        _gateway = gateway;
        _secrets = secrets;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> List(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        return board is null ? NotFound() : Ok(board.McpConnections);
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect(string boardId, [FromBody] ConnectRequest req, CancellationToken ct)
    {
        // NB: never log req.Token — it is a third-party credential.
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();

        var entry = McpCatalog.Find(req.CatalogId);
        if (entry is null) return BadRequest(new { error = "UnknownIntegration" });

        // Validate the credential by connecting + listing tools BEFORE storing anything.
        try
        {
            await _gateway.ListToolsAsync(
                new McpServerTarget(entry.Endpoint, entry.AuthHeader, entry.AuthScheme, req.Token), ct);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "ValidationFailed", detail = ex.Message });
        }

        var connectionId = Guid.NewGuid().ToString();
        var secretName = $"mcp-{boardId}-{connectionId}";
        await _secrets.SetSecretAsync(secretName, req.Token, ct);

        var conn = new McpConnection
        {
            ConnectionId = connectionId,
            CatalogId = entry.Id,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? entry.DisplayName : req.DisplayName!,
            SecretName = secretName,
            Status = McpConnectionStatus.Connected,
            LastValidatedAt = DateTimeOffset.UtcNow,
            CreatedBy = UserId,
        };
        board.McpConnections.Add(conn);
        await _cosmos.UpdateBoardAsync(board, ct);
        return Ok(conn);
    }

    [HttpPost("{connectionId}/validate")]
    public async Task<IActionResult> Validate(string boardId, string connectionId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var conn = board.McpConnections.FirstOrDefault(c => c.ConnectionId == connectionId);
        if (conn is null) return NotFound();
        var entry = McpCatalog.Find(conn.CatalogId);
        if (entry is null) return BadRequest(new { error = "UnknownIntegration" });

        var token = await _secrets.GetSecretAsync(conn.SecretName, ct);
        try
        {
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Credential missing.");
            await _gateway.ListToolsAsync(new McpServerTarget(entry.Endpoint, entry.AuthHeader, entry.AuthScheme, token), ct);
            conn.Status = McpConnectionStatus.Connected;
        }
        catch
        {
            conn.Status = McpConnectionStatus.Error;
        }
        conn.LastValidatedAt = DateTimeOffset.UtcNow;
        await _cosmos.UpdateBoardAsync(board, ct);
        return Ok(conn);
    }

    [HttpDelete("{connectionId}")]
    public async Task<IActionResult> Disconnect(string boardId, string connectionId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return NotFound();
        var conn = board.McpConnections.FirstOrDefault(c => c.ConnectionId == connectionId);
        if (conn is null) return NotFound();

        board.McpConnections.Remove(conn);
        await _cosmos.UpdateBoardAsync(board, ct);
        // Best-effort secret cleanup (empty value removes the key per FakeSecretProvider/KV convention).
        try { await _secrets.SetSecretAsync(conn.SecretName, string.Empty, ct); } catch { /* ignore */ }
        return NoContent();
    }
}
