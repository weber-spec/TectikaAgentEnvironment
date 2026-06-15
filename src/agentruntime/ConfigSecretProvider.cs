using Microsoft.Extensions.Configuration;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.AgentRuntime;

/// <summary>Dev-mode fallback: always returns GitHub:DevPat regardless of secret name.</summary>
public sealed class ConfigSecretProvider : ISecretProvider
{
    private readonly string _devPat;

    public ConfigSecretProvider(IConfiguration config)
    {
        _devPat = config["GitHub:DevPat"] ?? "";
    }

    public Task<string> GetSecretAsync(string name, CancellationToken ct) =>
        Task.FromResult(_devPat);

    public Task SetSecretAsync(string name, string value, CancellationToken ct) =>
        Task.CompletedTask;
}
