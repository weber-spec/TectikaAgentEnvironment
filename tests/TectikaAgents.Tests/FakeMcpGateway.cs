using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.Core.Interfaces;

public sealed class FakeMcpGateway : IMcpGateway
{
    public bool ThrowOnList { get; set; }
    public McpServerTarget? LastTarget { get; private set; }
    public string? LastTool { get; private set; }
    public string? LastArgsJson { get; private set; }
    public string Result { get; set; } = "{\"ok\":true}";

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(McpServerTarget target, CancellationToken ct)
    {
        LastTarget = target;
        if (ThrowOnList) throw new System.Exception("auth failed");
        return Task.FromResult<IReadOnlyList<McpToolInfo>>(new[] { new McpToolInfo("list_channels", null) });
    }

    public Task<string> CallToolAsync(McpServerTarget target, string toolName, string argumentsJson, CancellationToken ct)
    {
        LastTarget = target; LastTool = toolName; LastArgsJson = argumentsJson;
        return Task.FromResult(Result);
    }
}
