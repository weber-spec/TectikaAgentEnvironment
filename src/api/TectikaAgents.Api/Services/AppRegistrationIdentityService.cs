using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.Api.Services;

/// <summary>
/// MVP identity service — App Registration + Managed Identity.
/// להחליף ב-EntraAgentIdService כש-Agent ID יגיע GA.
/// </summary>
public class AppRegistrationIdentityService : IAgentIdentityService
{
    private readonly AzureAdSettings _settings;
    private readonly DefaultAzureCredential _credential;

    public AppRegistrationIdentityService(IOptions<AzureAdSettings> settings)
    {
        _settings = settings.Value;
        _credential = new DefaultAzureCredential();
    }

    public async Task<string> GetServiceTokenAsync(string[] scopes, CancellationToken ct = default)
    {
        var tokenRequest = new TokenRequestContext(scopes);
        var token = await _credential.GetTokenAsync(tokenRequest, ct);
        return token.Token;
    }

    public async Task<string> GetOboTokenAsync(string userAccessToken, string[] scopes, CancellationToken ct = default)
    {
        // OBO: exchange user token → downstream resource token
        // Microsoft.Identity.Web מנהל את זה דרך ITokenAcquisition — כאן placeholder ל-MVP
        // ב-MVP: הסוכנים פועלים ב-role identity. OBO יתווסף ב-Phase 2.
        await Task.CompletedTask;
        throw new NotImplementedException("OBO flow will be implemented in Phase 2. Use GetServiceTokenAsync for MVP.");
    }

    public string GetIdentityLabel(string? userPrincipalName = null) =>
        userPrincipalName is not null
            ? $"obo:{userPrincipalName}"
            : $"role:tectika-agents-identity";
}
