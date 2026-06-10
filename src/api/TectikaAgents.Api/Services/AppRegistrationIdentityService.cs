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
    private readonly ILogger<AppRegistrationIdentityService> _logger;

    public AppRegistrationIdentityService(IOptions<AzureAdSettings> settings, ILogger<AppRegistrationIdentityService> logger)
    {
        _settings = settings.Value;
        _credential = new DefaultAzureCredential();
        _logger = logger;
    }

    public async Task<string> GetServiceTokenAsync(string[] scopes, CancellationToken ct = default)
    {
        _logger.LogInformation("[Identity] acquiring service token scopes={ScopeCount}", scopes.Length);
        try
        {
            var tokenRequest = new TokenRequestContext(scopes);
            var token = await _credential.GetTokenAsync(tokenRequest, ct);
            _logger.LogInformation("[Identity] service token acquired scopes={ScopeCount} expiresOn={ExpiresOn}", scopes.Length, token.ExpiresOn);
            return token.Token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Identity] failed acquiring service token scopes={ScopeCount}", scopes.Length);
            throw;
        }
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
