using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.AgentRuntime;

public sealed class KeyVaultSecretProvider : ISecretProvider
{
    private readonly SecretClient _client;

    public KeyVaultSecretProvider(IConfiguration config)
    {
        var uri = config["KeyVault:VaultUri"]
            ?? throw new InvalidOperationException("KeyVault:VaultUri is not configured.");
        _client = new SecretClient(new Uri(uri), new DefaultAzureCredential());
    }

    public async Task<string> GetSecretAsync(string name, CancellationToken ct)
    {
        var response = await _client.GetSecretAsync(name, cancellationToken: ct);
        return response.Value.Value;
    }

    public async Task SetSecretAsync(string name, string value, CancellationToken ct)
    {
        await _client.SetSecretAsync(name, value, ct);
    }
}
