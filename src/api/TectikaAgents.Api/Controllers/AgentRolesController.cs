using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/agentroles")]
[Authorize]
public class AgentRolesController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly IAgentProvisioner _provisioner;

    public AgentRolesController(ICosmosDbService cosmos, IAgentProvisioner provisioner)
    {
        _cosmos = cosmos;
        _provisioner = provisioner;
    }

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
        var sync = await _provisioner.EnsureAgentAsync(role, ct);  // mutates role.FoundryAgentId/Hash on success
        // Intentional: persist the role even when sync fails (EnsureAgentAsync returns Synced=false rather
        // than throwing). The user keeps their edits and sees a "not synced" indicator; because the stored
        // hash still won't match, the next save retries provisioning. Returns the saved role + sync state.
        var saved = await _cosmos.UpsertAgentRoleAsync(role, ct);
        return Ok(new { role = saved, synced = sync.Synced, error = sync.Error });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var role = await _cosmos.GetAgentRoleAsync(TenantId, id, ct);
        if (role is null) return NotFound();
        await _provisioner.DeleteAgentAsync(role.FoundryAgentId, ct);
        await _cosmos.DeleteAgentRoleAsync(TenantId, id, ct);
        return NoContent();
    }
}
