using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

/// <summary>Manage the board's Email (Resend) sending domains + default sender. Proxies live to the Resend
/// Domains API using the connection's Key Vault key (never sent to the client). Scoped to the board's single
/// Connected `email` connection.</summary>
[ApiController]
[Route("api/boards/{boardId}/email")]
[Authorize]
public class BoardEmailController : ControllerBase
{
    public sealed record CreateDomainRequest(string Name);
    public sealed record SetFromRequest(string From);

    private readonly ICosmosDbService _cosmos;
    private readonly ISecretProvider _secrets;
    private readonly IResendDomainsClient _domains;

    public BoardEmailController(ICosmosDbService cosmos, ISecretProvider secrets, IResendDomainsClient domains)
    {
        _cosmos = cosmos; _secrets = secrets; _domains = domains;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    private async Task<(Connection conn, string apiKey)?> ResolveAsync(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return null;
        // The board's email connection = the enabled binding whose tenant connection is a Connected "email".
        var enabled = board.Connections.Select(b => b.ConnectionId).ToHashSet();
        var conns = await _cosmos.GetConnectionsAsync(TenantId, ct);
        var conn = conns.FirstOrDefault(c => enabled.Contains(c.Id) && c.CatalogId == "email" && c.Status == ConnectionStatus.Connected);
        if (conn is null) return null;
        var apiKey = await _secrets.GetSecretAsync(conn.SecretName, ct);
        if (string.IsNullOrEmpty(apiKey)) return null;
        return (conn, apiKey);
    }

    private IActionResult NoEmail() =>
        NotFound(new { error = "EmailNotConnected", detail = "Connect the Email integration on this board first." });

    private IActionResult Upstream(System.Exception ex) =>
        StatusCode(StatusCodes.Status502BadGateway, new { error = "ResendFailed", detail = ex.Message });

    [HttpGet("domains")]
    public async Task<IActionResult> ListDomains(string boardId, CancellationToken ct)
    {
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { return Ok(await _domains.ListAsync(r.Value.apiKey, ct)); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpPost("domains")]
    public async Task<IActionResult> CreateDomain(string boardId, [FromBody] CreateDomainRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "InvalidDomain" });
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { return Ok(await _domains.CreateAsync(r.Value.apiKey, req.Name.Trim(), ct)); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpGet("domains/{domainId}")]
    public async Task<IActionResult> GetDomain(string boardId, string domainId, CancellationToken ct)
    {
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { return Ok(await _domains.GetAsync(r.Value.apiKey, domainId, ct)); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpPost("domains/{domainId}/verify")]
    public async Task<IActionResult> VerifyDomain(string boardId, string domainId, CancellationToken ct)
    {
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { await _domains.VerifyAsync(r.Value.apiKey, domainId, ct); return Ok(await _domains.GetAsync(r.Value.apiKey, domainId, ct)); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpDelete("domains/{domainId}")]
    public async Task<IActionResult> DeleteDomain(string boardId, string domainId, CancellationToken ct)
    {
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { await _domains.DeleteAsync(r.Value.apiKey, domainId, ct); return NoContent(); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpPut("from")]
    public async Task<IActionResult> SetFrom(string boardId, [FromBody] SetFromRequest req, CancellationToken ct)
    {
        var from = (req.From ?? string.Empty).Trim();
        if (!from.Contains('@')) return BadRequest(new { error = "InvalidFrom", detail = "Enter a valid email address." });
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        r.Value.conn.Metadata["defaultFrom"] = from;
        await _cosmos.UpsertConnectionAsync(r.Value.conn, ct);
        return Ok(new { defaultFrom = from });
    }
}
