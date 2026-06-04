using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TectikaAgents.Api.Auth;

/// <summary>
/// Authentication handler used only when "MockDatabase:Enabled" is true. It always succeeds with a
/// fixed dev identity, so the frontend can call the [Authorize]-gated API without a real Azure AD
/// token. The injected claims (<c>tid</c>, <c>preferred_username</c>) match the tenant/owner the
/// mock data is seeded under, keeping controllers and seed data in sync. Never registered in
/// production (the flag is off → real Microsoft Identity auth is used instead).
/// </summary>
public class MockAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Mock";

    private const string DevTenantId = "default";
    private const string DevUser = "dev@tectika.com";

    public MockAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("tid", DevTenantId),
            new Claim("preferred_username", DevUser),
            new Claim(ClaimTypes.Name, DevUser),
            new Claim(ClaimTypes.NameIdentifier, DevUser),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
