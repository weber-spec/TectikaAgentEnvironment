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
    private readonly NotificationRepository _notificationRepo;
    private readonly NotificationConnectionManager _notificationManager;

    public AgentRolesController(
        ICosmosDbService cosmos,
        IAgentProvisioner provisioner,
        NotificationRepository notificationRepo,
        NotificationConnectionManager notificationManager)
    {
        _cosmos = cosmos;
        _provisioner = provisioner;
        _notificationRepo = notificationRepo;
        _notificationManager = notificationManager;
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
        // Carry the Foundry identity forward from the stored role: the client never sends
        // FoundryAgentId/Hash, and the agent name (FoundryAgentId) must stay stable across edits and
        // renames — otherwise EnsureAgent would treat each save as new and orphan + recreate the agent.
        var existing = await _cosmos.GetAgentRoleAsync(TenantId, role.Id, ct);
        if (existing is not null)
        {
            role.FoundryAgentId = existing.FoundryAgentId;
            role.FoundryAgentHash = existing.FoundryAgentHash;
        }
        var sync = await _provisioner.EnsureAgentAsync(role, ct);  // mutates role.FoundryAgentId/Hash on success
        // Intentional: persist the role even when sync fails (EnsureAgentAsync returns Synced=false rather
        // than throwing). The user keeps their edits and sees a "not synced" indicator; because the stored
        // hash still won't match, the next save retries provisioning. Returns the saved role + sync state.
        var saved = await _cosmos.UpsertAgentRoleAsync(role, ct);

        var isNew = existing is null;
        if (isNew)
        {
            var notif = new NotificationDocument
            {
                TenantId = TenantId,
                Type = "agent",
                Title = $"Agent \"{saved.DisplayName}\" created",
                SourceEventType = AgentEvent.Types.AgentCreated,
            };
            await _notificationRepo.SaveAsync(notif, ct);
            await _notificationManager.BroadcastAsync(notif, ct);
        }

        return Ok(new { role = saved, synced = sync.Synced, error = sync.Error });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var role = await _cosmos.GetAgentRoleAsync(TenantId, id, ct);
        if (role is null) return NotFound();
        await _provisioner.DeleteAgentAsync(role.FoundryAgentId, ct);
        await _cosmos.DeleteAgentRoleAsync(TenantId, id, ct);

        var notif = new NotificationDocument
        {
            TenantId = TenantId,
            Type = "agent",
            Title = $"Agent \"{role.DisplayName}\" deleted",
            SourceEventType = AgentEvent.Types.AgentDeleted,
        };
        await _notificationRepo.SaveAsync(notif, ct);
        await _notificationManager.BroadcastAsync(notif, ct);

        return NoContent();
    }
}
