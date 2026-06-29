using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Resolves the <see cref="IAgentRuntime"/> for a role based on its
/// <see cref="AgentRole.ExecutionEngine"/> (Foundry vs Claude Code). Keeps engine-awareness in one
/// place so the round activity stays engine-agnostic.</summary>
public interface IAgentRuntimeFactory
{
    IAgentRuntime ForRole(AgentRole role);
}
