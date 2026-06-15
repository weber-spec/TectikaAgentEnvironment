using System.Security.Cryptography;
using System.Text;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Deterministic hash of the agent-defining fields, to detect when a Foundry agent needs updating.</summary>
public static class AgentInstructionsHash
{
    public static string Compute(string systemPrompt, string model, string toolsVersion,
        GitHubPermissions? github = null)
    {
        var gh = github is null ? ""
            : $"gh:{github.CanRead}:{github.CanCreateBranch}:{github.CanPush}:{github.CanCreatePr}";
        var bytes = Encoding.UTF8.GetBytes($"{model}\n{toolsVersion}\n{gh}\n{systemPrompt}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
