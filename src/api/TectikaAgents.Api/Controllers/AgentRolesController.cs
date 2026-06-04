using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/agentroles")]
[Authorize]
public class AgentRolesController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;

    public AgentRolesController(ICosmosDbService cosmos) => _cosmos = cosmos;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        Ok(await _cosmos.GetAgentRolesAsync(TenantId, ct));

    [HttpGet("{roleId}")]
    public async Task<IActionResult> Get(string roleId, CancellationToken ct)
    {
        var roles = await _cosmos.GetAgentRolesAsync(TenantId, ct);
        var role = roles.FirstOrDefault(r => r.Id == roleId);
        return role is null ? NotFound() : Ok(role);
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] AgentRole role, CancellationToken ct)
    {
        role.TenantId = TenantId;
        role.UpdatedAt = DateTimeOffset.UtcNow;
        var saved = await _cosmos.UpsertAgentRoleAsync(role, ct);
        return Ok(saved);
    }
}
