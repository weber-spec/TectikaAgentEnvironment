using System.Text.Json;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Dispatches namespaced MCP tool calls (`{catalogId}__{tool}`) to the connected board server.
/// Resolves the board's connection + Key Vault token, enforces the write opt-in, and forwards through
/// IMcpGateway. Plugs into RoundExecutor via CanHandle/ExecuteAsync (same shape as the GitHub executor).</summary>
public sealed class McpToolExecutor
{
    private readonly IMcpGateway _gateway;
    private readonly ISecretProvider _secrets;

    public McpToolExecutor(IMcpGateway gateway, ISecretProvider secrets)
    {
        _gateway = gateway;
        _secrets = secrets;
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
