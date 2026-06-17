using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Re-syncs an agent's Foundry definition if it's stale (e.g. the tool schema changed and the
/// agent was never re-saved). EnsureAgentAsync internally republishes only when its hash differs;
/// this returns whether the role's stored hash actually changed, so the caller persists it once.</summary>
public static class AgentSelfHeal
{
    public static async Task<bool> EnsureCurrentAsync(IAgentProvisioner provisioner, AgentRole role, CancellationToken ct = default)
    {
        var before = role.FoundryAgentHash;
        var sync = await provisioner.EnsureAgentAsync(role, ct);
        return sync.Synced && role.FoundryAgentHash != before;
    }
}
