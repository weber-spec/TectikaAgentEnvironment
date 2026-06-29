namespace TectikaAgents.Core.Interfaces;

/// <summary>Where + how to reach one remote MCP server. Token is the resolved secret value
/// (never logged). AuthScheme is "" for a raw header value, or e.g. "Bearer".</summary>
public sealed record McpServerTarget(string Endpoint, string AuthHeader, string AuthScheme, string Token);

/// <summary>One tool advertised by an MCP server (used only for connect-time validation today).</summary>
public sealed record McpToolInfo(string Name, string? Description);

/// <summary>The single seam over the MCP client SDK. Implemented for real by McpGateway and faked in tests.</summary>
public interface IMcpGateway
{
    /// <summary>Connect and list the server's tools. Throws on auth/transport failure (used to validate a connection).</summary>
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(McpServerTarget target, CancellationToken ct);

    /// <summary>Call one tool. <paramref name="argumentsJson"/> is the raw JSON object the model produced.
    /// Returns the tool result serialized as a JSON string.</summary>
    Task<string> CallToolAsync(McpServerTarget target, string toolName, string argumentsJson, CancellationToken ct);
}
