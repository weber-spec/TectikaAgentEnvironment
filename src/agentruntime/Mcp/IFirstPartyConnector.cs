using System.Text.Json;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>A curated catalog integration whose tools execute IN-PROCESS against a provider's API,
/// rather than through a remote MCP server reached via IMcpGateway. This is the path for providers that
/// expose only a REST API (or whose MCP server can't take a per-board token), and mirrors how the
/// first-party GitHub executor works. The board's resolved credential is passed per call and never logged.</summary>
public interface IFirstPartyConnector
{
    /// <summary>The <see cref="McpCatalog"/> entry id this connector backs (e.g. "email").</summary>
    string CatalogId { get; }

    /// <summary>Execute one of the entry's tools. <paramref name="token"/> is the board's resolved secret value.
    /// The write opt-in and connection resolution are enforced by the caller (McpToolExecutor) before this runs.
    /// Returns the tool result serialized as a JSON string.</summary>
    Task<string> CallAsync(string toolName, JsonElement args, string token, CancellationToken ct);
}
