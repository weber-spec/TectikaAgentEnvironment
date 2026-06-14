using System.Security.Cryptography;
using System.Text;

namespace TectikaAgents.AgentRuntime;

/// <summary>Deterministic hash of the agent-defining fields, to detect when a Foundry agent needs updating.</summary>
public static class AgentInstructionsHash
{
    public static string Compute(string systemPrompt, string model, string toolsVersion)
    {
        var bytes = Encoding.UTF8.GetBytes($"{model}\n{toolsVersion}\n \n{systemPrompt}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
