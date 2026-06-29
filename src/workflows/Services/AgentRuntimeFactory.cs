using Microsoft.Extensions.DependencyInjection;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>Picks the runtime per role's engine. Foundry resolves the registered <see cref="IAgentRuntime"/>
/// (the real Foundry runtime, or the mock in dev). ClaudeCode resolves the concrete
/// <see cref="ClaudeCodeAgentRuntime"/> when registered, falling back to the default runtime (so mock-agent
/// mode exercises both engines through the mock).</summary>
public sealed class AgentRuntimeFactory : IAgentRuntimeFactory
{
    private readonly IServiceProvider _sp;

    public AgentRuntimeFactory(IServiceProvider sp) => _sp = sp;

    public IAgentRuntime ForRole(AgentRole role) => role.ExecutionEngine switch
    {
        ExecutionEngine.ClaudeCode =>
            (IAgentRuntime?)_sp.GetService<ClaudeCodeAgentRuntime>() ?? _sp.GetRequiredService<IAgentRuntime>(),
        _ => _sp.GetRequiredService<IAgentRuntime>(),
    };
}
