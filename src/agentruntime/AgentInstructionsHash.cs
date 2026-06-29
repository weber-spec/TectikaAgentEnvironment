using System.Security.Cryptography;
using System.Text;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Deterministic hash of the agent-defining fields, to detect when a Foundry agent needs updating.</summary>
public static class AgentInstructionsHash
{
    public static string Compute(string systemPrompt, string model, string toolsVersion,
        AgentPermissions permissions, GitHubPermissions? github = null,
        IReadOnlyList<string>? mcpEnabled = null, IReadOnlyList<string>? mcpWriteEnabled = null)
    {
        var gh = github is null ? "" : $"gh:{github.CanRead}";
        var ws = $"ws:{permissions.CanUseWorkspace}";
        // Order-independent: a role's enabled/write lists are sets, not sequences.
        var mcp = $"mcp:{Join(mcpEnabled)}|mcpw:{Join(mcpWriteEnabled)}|cat:{McpCatalog.Version}";
        var bytes = Encoding.UTF8.GetBytes($"{model}\n{toolsVersion}\n{ws}|{gh}|{mcp}\n{systemPrompt}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Join(IReadOnlyList<string>? items) =>
        items is null || items.Count == 0 ? "" : string.Join(",", items.OrderBy(x => x, StringComparer.Ordinal));
}
