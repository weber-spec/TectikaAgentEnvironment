using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Api.Controllers;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

public class BoardEmailControllerTests
{
    private sealed class FakeDomains : IResendDomainsClient
    {
        public string? LastApiKey, LastCreatedName, LastVerifiedId;
        public ResendDomain ToReturn = new("d1", "acme.com", "not_started",
            new[] { new ResendDnsRecord("DKIM", "resend._domainkey", "TXT", "Auto", "not_started", "p=abc", null) });
        public Task<IReadOnlyList<ResendDomain>> ListAsync(string apiKey, CancellationToken ct)
        { LastApiKey = apiKey; return Task.FromResult<IReadOnlyList<ResendDomain>>(new[] { ToReturn }); }
        public Task<ResendDomain> CreateAsync(string apiKey, string name, CancellationToken ct)
        { LastApiKey = apiKey; LastCreatedName = name; return Task.FromResult(ToReturn); }
        public Task<ResendDomain> GetAsync(string apiKey, string id, CancellationToken ct) => Task.FromResult(ToReturn);
        public Task VerifyAsync(string apiKey, string id, CancellationToken ct) { LastVerifiedId = id; return Task.CompletedTask; }
        public Task DeleteAsync(string apiKey, string id, CancellationToken ct) => Task.CompletedTask;
    }

    private static (BoardEmailController ctrl, FakeCosmosForBoard cosmos, FakeSecretProvider secrets, string boardId) Build(FakeDomains domains, bool withEmail = true)
    {
        var cosmos = new FakeCosmosForBoard();
        var secrets = new FakeSecretProvider();
        var board = cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "eli" }).Result;
        if (withEmail)
        {
            secrets.Store["sec1"] = "re_key";
            cosmos.UpsertConnectionAsync(new Connection
            {
                Id = "conn-email", TenantId = "t1", CatalogId = "email", Category = "agent-tool",
                SecretName = "sec1", Status = ConnectionStatus.Connected,
            }).Wait();
            board.Connections.Add(new BoardConnectionBinding { ConnectionId = "conn-email" });
            cosmos.UpdateBoardAsync(board).Wait();
        }
        var ctrl = new BoardEmailController(cosmos, secrets, domains);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tid", "t1") }, "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return (ctrl, cosmos, secrets, board.Id);
    }

    [Fact]
    public async Task Create_domain_uses_connection_key_and_returns_records()
    {
        var domains = new FakeDomains();
        var (ctrl, _, _, boardId) = Build(domains);
        var res = await ctrl.CreateDomain(boardId, new BoardEmailController.CreateDomainRequest("acme.com"), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);
        Assert.Equal("re_key", domains.LastApiKey);
        Assert.Equal("acme.com", domains.LastCreatedName);
        var d = Assert.IsType<ResendDomain>(ok.Value);
        Assert.Single(d.Records!);
    }

    [Fact]
    public async Task Endpoints_404_when_no_email_connection()
    {
        var (ctrl, _, _, boardId) = Build(new FakeDomains(), withEmail: false);
        var res = await ctrl.ListDomains(boardId, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(res);
    }

    [Fact]
    public async Task Verify_calls_client_with_domain_id()
    {
        var domains = new FakeDomains();
        var (ctrl, _, _, boardId) = Build(domains);
        await ctrl.VerifyDomain(boardId, "d1", CancellationToken.None);
        Assert.Equal("d1", domains.LastVerifiedId);
    }

    [Fact]
    public async Task Set_from_persists_default_from_on_connection()
    {
        var (ctrl, cosmos, _, boardId) = Build(new FakeDomains());
        var res = await ctrl.SetFrom(boardId, new BoardEmailController.SetFromRequest("Agents <a@acme.com>"), CancellationToken.None);
        Assert.IsType<OkObjectResult>(res);
        var conn = await cosmos.GetConnectionAsync("t1", "conn-email", CancellationToken.None);
        Assert.Equal("Agents <a@acme.com>", conn!.Metadata["defaultFrom"]);
    }

    [Fact]
    public async Task Set_from_rejects_address_without_at()
    {
        var (ctrl, _, _, boardId) = Build(new FakeDomains());
        var res = await ctrl.SetFrom(boardId, new BoardEmailController.SetFromRequest("not-an-email"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(res);
    }
}
