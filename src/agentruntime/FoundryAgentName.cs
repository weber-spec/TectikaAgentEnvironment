namespace TectikaAgents.AgentRuntime;

/// <summary>Generates a stable, Foundry-valid agent name (lowercase alphanumeric + hyphens).
/// This becomes the agent's permanent identity (stored in AgentRole.FoundryAgentId) and is
/// deliberately decoupled from the user's display name, so renames never recreate the agent.</summary>
public static class FoundryAgentName
{
    public static string New() => $"agent-{Guid.NewGuid():N}"[..18]; // "agent-" + 12 hex
}
