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
    private readonly ISecretProvider _secrets;
    private readonly NotificationRepository _notificationRepo;
    private readonly NotificationConnectionManager _notificationManager;

    public AgentRolesController(
        ICosmosDbService cosmos,
        IAgentProvisioner provisioner,
        ISecretProvider secrets,
        NotificationRepository notificationRepo,
        NotificationConnectionManager notificationManager)
    {
        _cosmos = cosmos;
        _provisioner = provisioner;
        _secrets = secrets;
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
    public async Task<IActionResult> Upsert([FromBody] AgentRoleUpsertRequest req, CancellationToken ct)
    {
        var role = req.Role;
        role.TenantId = TenantId;
        role.UpdatedAt = DateTimeOffset.UtcNow;
        var existing = await _cosmos.GetAgentRoleAsync(TenantId, role.Id, ct);

        // The agent's model provider comes from its chosen model connection (Connections page): Foundry is the
        // system default; an Anthropic connection selects the Claude Code engine and supplies the credential
        // secret. The credential itself is never entered here — it lives on the connection.
        if (string.IsNullOrEmpty(role.ModelConnectionId) || role.ModelConnectionId == ConnectionsController.FoundrySystemId)
        {
            role.ExecutionEngine = ExecutionEngine.Foundry;
            role.ApiKeySecretName = null;
        }
        else
        {
            var modelConn = await _cosmos.GetConnectionAsync(TenantId, role.ModelConnectionId, ct);
            if (modelConn is null || modelConn.CatalogId != "anthropic")
                return BadRequest(new { error = "InvalidModelConnection", detail = "Select a model provider on the Connections page." });
            role.ExecutionEngine = ExecutionEngine.ClaudeCode;
            role.ApiKeySecretName = modelConn.SecretName;
            role.ClaudeAuth = Enum.TryParse<ClaudeAuthMode>(modelConn.Metadata.GetValueOrDefault("claudeAuth"), out var m) ? m : ClaudeAuthMode.ApiKey;
        }

        bool synced;
        string? syncError;
        if (role.ExecutionEngine == ExecutionEngine.ClaudeCode)
        {
            // Claude Code roles are not provisioned in Foundry; the credential is the Anthropic connection's
            // Key Vault secret (resolved above), so nothing to store here.
            role.FoundryAgentId = existing?.FoundryAgentId;
            role.FoundryAgentHash = existing?.FoundryAgentHash;
            synced = !string.IsNullOrEmpty(role.ApiKeySecretName);
            syncError = synced ? null : "Select a Claude (Anthropic) connection on the Connections page to power this agent.";
        }
        else
        {
            // Carry the Foundry identity forward from the stored role: the client never sends
            // FoundryAgentId/Hash, and the agent name (FoundryAgentId) must stay stable across edits and
            // renames — otherwise EnsureAgent would treat each save as new and orphan + recreate the agent.
            if (existing is not null)
            {
                role.FoundryAgentId = existing.FoundryAgentId;
                role.FoundryAgentHash = existing.FoundryAgentHash;
            }
            var sync = await _provisioner.EnsureAgentAsync(role, ct);  // mutates role.FoundryAgentId/Hash on success
            synced = sync.Synced;
            syncError = sync.Error;
        }

        // Intentional: persist the role even when sync fails. The user keeps their edits and sees a
        // "not synced" indicator; the next save retries. Returns the saved role + sync state.
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

        return Ok(new { role = saved, synced, error = syncError });
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

/// <summary>Upsert body: the role plus an OPTIONAL transient Anthropic API key (ClaudeCode engine only).
/// The key is stored in Key Vault and never persisted on the role document.</summary>
public record AgentRoleUpsertRequest(AgentRole Role, string? AnthropicApiKey);
