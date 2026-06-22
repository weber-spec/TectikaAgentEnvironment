namespace TectikaAgents.Core.Interfaces;

/// <summary>Lists the model / deployment names available for agents (shown in the model picker).
/// Mock impl returns a static list; the Foundry impl enumerates the project's deployments.</summary>
public interface IModelCatalog
{
    /// <summary>Available model names. May throw if a real backing store (Foundry) is unreachable.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}
