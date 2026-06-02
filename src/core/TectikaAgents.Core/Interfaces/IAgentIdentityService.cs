namespace TectikaAgents.Core.Interfaces;

/// <summary>
/// Abstraction layer לזהות סוכנים — מאפשר מעבר מ-App Registration ל-Entra Agent ID כשיגיע GA.
/// </summary>
public interface IAgentIdentityService
{
    /// <summary>
    /// מחזיר access token למשאב downstream בשם המשתמש (OBO flow).
    /// </summary>
    Task<string> GetOboTokenAsync(string userAccessToken, string[] scopes, CancellationToken ct = default);

    /// <summary>
    /// מחזיר access token בזהות-תפקיד (Managed Identity / service identity).
    /// </summary>
    Task<string> GetServiceTokenAsync(string[] scopes, CancellationToken ct = default);

    /// <summary>
    /// שם הזהות לצורך audit log ("obo:user@x.com" / "role:backend-developer").
    /// </summary>
    string GetIdentityLabel(string? userPrincipalName = null);
}
