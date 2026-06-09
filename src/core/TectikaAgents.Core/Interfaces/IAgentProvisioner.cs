using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Manages the lifecycle of a Foundry agent for a role (used by the Agents tab).</summary>
public interface IAgentProvisioner
{
    /// <summary>Create the agent if absent, update it if prompt/model changed. Returns the agent id + sync state.</summary>
    Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default);

    /// <summary>Delete the Foundry agent. No-op if id is null/empty or already gone.</summary>
    Task DeleteAgentAsync(string? foundryAgentId, CancellationToken ct = default);
}
