using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Lists the Claude model ids available for a given Anthropic connection, fetched live from
/// Anthropic's <c>GET /v1/models</c> using the connection's stored credential. Unlike
/// <see cref="IModelCatalog"/> it NEVER throws — on OAuth-only connections or any failure it degrades to a
/// curated fallback list, so the model picker always has a usable set.</summary>
public interface IClaudeModelCatalog
{
    /// <summary>Available Claude model ids for the connection (live when possible, curated fallback otherwise).</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(Connection conn, CancellationToken ct = default);
}
