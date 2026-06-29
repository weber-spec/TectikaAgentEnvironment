using System.Text.Json;
using ModelContextProtocol.Client;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Real IMcpGateway over the ModelContextProtocol client SDK (remote Streamable HTTP/SSE).
/// The auth token is injected as a request header; it is never logged.</summary>
public sealed class McpGateway : IMcpGateway
{
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(McpServerTarget target, CancellationToken ct)
    {
        await using var client = await ConnectAsync(target, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return tools.Select(t => new McpToolInfo(t.Name, t.Description)).ToList();
    }

    public async Task<string> CallToolAsync(McpServerTarget target, string toolName, string argumentsJson, CancellationToken ct)
    {
        await using var client = await ConnectAsync(target, ct);
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) ?? new();
        var result = await client.CallToolAsync(toolName, (IReadOnlyDictionary<string, object?>)args, cancellationToken: ct);
        return JsonSerializer.Serialize(result.Content);
    }

    private static async Task<McpClient> ConnectAsync(McpServerTarget target, CancellationToken ct)
    {
        var headerValue = string.IsNullOrEmpty(target.AuthScheme) ? target.Token : $"{target.AuthScheme} {target.Token}";
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(target.Endpoint),
            AdditionalHeaders = new Dictionary<string, string> { [target.AuthHeader] = headerValue },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }
}
