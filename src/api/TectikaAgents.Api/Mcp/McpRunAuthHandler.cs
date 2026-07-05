using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Mcp;

namespace TectikaAgents.Api.Mcp;

/// <summary>Authenticates the board-tools MCP endpoint with the per-run HMAC token minted by the workflows
/// runtime (see <see cref="RunTokenCodec"/>). On success the resolved <see cref="RunContext"/> is stashed on
/// the request so tool handlers scope every call to the token's board/task/tenant — never to arguments the
/// model supplies.</summary>
public sealed class McpRunAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "McpRun";
    public const string RunContextItem = "McpRunContext";

    private readonly IConfiguration _config;

    public McpRunAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, IConfiguration config) : base(options, logger, encoder)
        => _config = config;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var key = _config["Mcp:SigningKey"];
        if (string.IsNullOrEmpty(key))
            return Task.FromResult(AuthenticateResult.Fail("MCP run-token signing key is not configured (Mcp:SigningKey)."));

        var token = header["Bearer ".Length..].Trim();
        var ctx = RunTokenCodec.TryValidate(token, key, DateTimeOffset.UtcNow);
        if (ctx is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired run token."));

        Context.Items[RunContextItem] = ctx;
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, ctx.RoleId));
        identity.AddClaim(new Claim("runId", ctx.RunId));
        identity.AddClaim(new Claim("boardId", ctx.BoardId));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
