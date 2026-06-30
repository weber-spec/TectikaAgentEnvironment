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
    private readonly IReadOnlyDictionary<string, IFirstPartyConnector> _connectors;

    public BoardMcpConnectionsController(ICosmosDbService cosmos, IMcpGateway gateway, ISecretProvider secrets,
        IEnumerable<IFirstPartyConnector>? connectors = null)
    {
        _cosmos = cosmos;
        _gateway = gateway;
        _secrets = secrets;
        _connectors = (connectors ?? Array.Empty<IFirstPartyConnector>())
            .ToDictionary(c => c.CatalogId, StringComparer.Ordinal);
    }

    /// <summary>Validate a credential before storing it. Throws if the token is rejected. Routes by backend:
    /// remote MCP entries connect + list tools; first-party entries validate through their connector.</summary>
    private async Task ValidateCredentialAsync(McpCatalog.CatalogEntry entry, string token, CancellationToken ct)
    {
        if (entry.Backend == McpBackend.FirstParty)
        {
            if (!_connectors.TryGetValue(entry.Id, out var connector))
                throw new InvalidOperationException("No connector is configured for this integration.");
            await connector.ValidateAsync(token, ct);
        }
        else
        {
            await _gateway.ListToolsAsync(
                new McpServerTarget(entry.Endpoint, entry.AuthHeader, entry.AuthScheme, token), ct);
        }
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

        // Validate the credential BEFORE storing anything (backend-aware: MCP server vs first-party connector).
        try
        {
            await ValidateCredentialAsync(entry, req.Token, ct);
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
            await ValidateCredentialAsync(entry, token, ct);
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
