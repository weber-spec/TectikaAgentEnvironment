using System.Security.Cryptography;
using System.Text;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Deterministic hash of the agent-defining fields, to detect when a Foundry agent needs updating.</summary>
public static class AgentInstructionsHash
{
    public static string Compute(string systemPrompt, string model, string toolsVersion,
        AgentPermissions permissions, GitHubPermissions? github = null)
    {
        var gh = github is null ? "" : $"gh:{github.CanRead}";
        var ws = $"ws:{permissions.CanUseWorkspace}";
        var bytes = Encoding.UTF8.GetBytes($"{model}\n{toolsVersion}\n{ws}|{gh}\n{systemPrompt}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
