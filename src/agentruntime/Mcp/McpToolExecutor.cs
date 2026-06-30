using System.Text.Json;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Dispatches namespaced catalog tool calls (`{catalogId}__{tool}`) for a board. Resolves the
/// board's connection + Key Vault token and enforces the write opt-in, then routes by the catalog entry's
/// backend: remote MCP servers go through IMcpGateway, first-party integrations through an IFirstPartyConnector.
/// Plugs into RoundExecutor via CanHandle/ExecuteAsync (same shape as the GitHub executor).</summary>
public sealed class McpToolExecutor
{
    private readonly IMcpGateway _gateway;
    private readonly ISecretProvider _secrets;
    private readonly IReadOnlyDictionary<string, IFirstPartyConnector> _connectors;

    public McpToolExecutor(IMcpGateway gateway, ISecretProvider secrets,
        IEnumerable<IFirstPartyConnector>? connectors = null)
    {
        _gateway = gateway;
        _secrets = secrets;
        _connectors = (connectors ?? Array.Empty<IFirstPartyConnector>())
            .ToDictionary(c => c.CatalogId, StringComparer.Ordinal);
    }

    public bool CanHandle(string toolName) =>
        McpToolNaming.TryParse(toolName, out var cid, out var tool)
        && McpCatalog.Find(cid)?.Tools.Any(t => t.Name == tool) == true;

    public async Task<string> ExecuteAsync(string toolName, JsonElement args,
        IReadOnlyList<McpConnection>? boardConnections, AgentRole? role, CancellationToken ct)
    {
        if (!McpToolNaming.TryParse(toolName, out var catalogId, out var tool))
            return Err($"'{toolName}' is not a valid MCP tool name.");

        var entry = McpCatalog.Find(catalogId);
        var def = entry?.Tools.FirstOrDefault(t => t.Name == tool);
        if (entry is null || def is null)
            return Err($"Unknown MCP tool '{toolName}'.");

        var conn = boardConnections?.FirstOrDefault(c =>
            c.CatalogId == catalogId && c.Status == McpConnectionStatus.Connected);
        if (conn is null)
            return Err($"{entry.DisplayName} is not connected to this board. Ask a board admin to connect it in Board Settings → Integrations.");

        if (def.IsWrite && !(role?.McpWriteEnabled.Contains(catalogId) ?? false))
            return Err($"Write actions for {entry.DisplayName} are not permitted for this agent.");

        try
        {
            var token = await _secrets.GetSecretAsync(conn.SecretName, ct);
            if (string.IsNullOrEmpty(token))
                return Err($"{entry.DisplayName} credential is missing or expired. Reconnect it in Board Settings.");

            if (entry.Backend == McpBackend.FirstParty)
            {
                if (!_connectors.TryGetValue(catalogId, out var connector))
                    return Err($"{entry.DisplayName} is unavailable (no connector is configured). Please contact support.");
                return await connector.CallAsync(tool, args, token, ct);
            }

            var target = new McpServerTarget(entry.Endpoint, entry.AuthHeader, entry.AuthScheme, token);
            return await _gateway.CallToolAsync(target, tool, args.GetRawText(), ct);
        }
        catch (Exception ex)
        {
            return Err($"{entry.DisplayName} call '{tool}' failed: {ex.Message}");
        }
    }

    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });
}
