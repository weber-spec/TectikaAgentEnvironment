# Email Sending-Domain Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a board admin add + verify a Resend sending domain in Tectika and set a default `from`, so agents can email any recipient.

**Architecture:** A new `BoardEmailController` proxies live to the Resend Domains API (via a thin `ResendDomainsClient`) using the board's email-connection API key from Key Vault — no domain state stored. The only new persisted field is `McpConnection.DefaultFrom`. `send_email`'s `from` becomes optional and defaults to it. A new "Sending domains" UI panel drives the flow.

**Tech Stack:** .NET 10 (xUnit, ASP.NET controllers, `IHttpClientFactory`), Next/React/TypeScript web, Node built-in test runner.

**Branch:** `feat/email-sending-domains` (already created off `main`).

---

## File Structure

- `src/core/TectikaAgents.Core/Models/McpConnection.cs` — add `DefaultFrom`.
- `src/agentruntime/Mcp/IFirstPartyConnector.cs` — `CallAsync` gains `McpConnection`.
- `src/agentruntime/Mcp/ResendEmailConnector.cs` — default-from in `send_email`.
- `src/agentruntime/Mcp/McpToolExecutor.cs` — pass `conn` through.
- `src/agentruntime/Mcp/McpCatalog.cs` — `from` optional; bump `Version`.
- `src/agentruntime/Mcp/ResendDomainsClient.cs` — NEW: `IResendDomainsClient` + impl + DTOs.
- `src/api/TectikaAgents.Api/Controllers/BoardEmailController.cs` — NEW: domain + from endpoints.
- `src/api/TectikaAgents.Api/Program.cs` — register `IResendDomainsClient`.
- `tests/TectikaAgents.Tests/…` — connector, client, controller tests; update `FakeFirstPartyConnector`.
- `src/web/tectika-board/src/lib/{types,api}.ts` — `api.email.*` + types.
- `src/web/tectika-board/src/components/board/settings/EmailDomainsPanel.tsx` — NEW panel.
- `src/web/tectika-board/src/components/board/settings/McpIntegrationsTab.tsx` — mount the panel.

---

## Task 1: Add `McpConnection.DefaultFrom`

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/McpConnection.cs`

- [ ] **Step 1: Add the field**

In `McpConnection`, after the `CreatedAt` line, add:

```csharp
    /// <summary>Default sender address for first-party email integrations (e.g. "Agents <agents@acme.com>").
    /// Used by send_email when the agent omits `from`. Must be on a domain verified in the provider account.</summary>
    [JsonPropertyName("defaultFrom")] public string? DefaultFrom { get; set; }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj -v q -nologo`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/McpConnection.cs
git commit -m "feat(core): McpConnection.DefaultFrom for email sender default"
```

---

## Task 2: Thread the resolved `McpConnection` into the connector seam

The connector needs the connection (for `DefaultFrom`). Change `IFirstPartyConnector.CallAsync` to accept it, update the impl, the executor call site, and the test fake. Behaviour is unchanged this task (no default-from logic yet) — this is a pure refactor that keeps tests green.

**Files:**
- Modify: `src/agentruntime/Mcp/IFirstPartyConnector.cs`
- Modify: `src/agentruntime/Mcp/ResendEmailConnector.cs`
- Modify: `src/agentruntime/Mcp/McpToolExecutor.cs`
- Modify: `tests/TectikaAgents.Tests/FakeFirstPartyConnector.cs`

- [ ] **Step 1: Change the interface**

In `IFirstPartyConnector.cs`, replace the `CallAsync` signature + doc:

```csharp
    /// <summary>Execute one of the entry's tools. <paramref name="token"/> is the board's resolved secret value
    /// and <paramref name="connection"/> the resolved per-board connection (for config such as DefaultFrom). The
    /// write opt-in and connection resolution are enforced by the caller (McpToolExecutor) before this runs.
    /// Returns the tool result serialized as a JSON string.</summary>
    Task<string> CallAsync(string toolName, JsonElement args, string token,
        TectikaAgents.Core.Models.McpConnection connection, CancellationToken ct);
```

- [ ] **Step 2: Update `ResendEmailConnector.CallAsync` signature**

In `ResendEmailConnector.cs`, change the method signature (body unchanged for now):

```csharp
    public async Task<string> CallAsync(string toolName, JsonElement args, string token,
        TectikaAgents.Core.Models.McpConnection connection, CancellationToken ct)
```

- [ ] **Step 3: Update the executor call site**

In `McpToolExecutor.cs`, the first-party branch currently reads:

```csharp
                return await connector.CallAsync(tool, args, token, ct);
```

Replace with (note `conn` is the already-resolved connection in scope):

```csharp
                return await connector.CallAsync(tool, args, token, conn, ct);
```

- [ ] **Step 4: Update the test fake**

In `tests/TectikaAgents.Tests/FakeFirstPartyConnector.cs`, add a `LastConnection` field and update `CallAsync`:

```csharp
    public TectikaAgents.Core.Models.McpConnection? LastConnection { get; private set; }

    public Task<string> CallAsync(string toolName, System.Text.Json.JsonElement args, string token,
        TectikaAgents.Core.Models.McpConnection connection, CancellationToken ct)
    {
        LastTool = toolName; LastToken = token; LastConnection = connection;
        return Task.FromResult(Result);
    }
```

(Delete the old `CallAsync` overload.)

- [ ] **Step 5: Run the existing MCP/connector tests to verify green**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~McpToolExecutor|FullyQualifiedName~ResendEmail" --nologo -v q`
Expected: PASS (all existing first-party routing + connector tests still pass).

- [ ] **Step 6: Commit**

```bash
git add src/agentruntime/Mcp/IFirstPartyConnector.cs src/agentruntime/Mcp/ResendEmailConnector.cs src/agentruntime/Mcp/McpToolExecutor.cs tests/TectikaAgents.Tests/FakeFirstPartyConnector.cs
git commit -m "refactor(agents): pass resolved McpConnection into IFirstPartyConnector.CallAsync"
```

---

## Task 3: `send_email` — optional `from`, default to `connection.DefaultFrom`

**Files:**
- Modify: `tests/TectikaAgents.Tests/ResendEmailConnectorTests.cs`
- Modify: `src/agentruntime/Mcp/ResendEmailConnector.cs`
- Modify: `src/agentruntime/Mcp/McpCatalog.cs`

- [ ] **Step 1: Write failing tests**

In `ResendEmailConnectorTests.cs`, the helper `ValidArgs` currently includes `from`. Add these tests (and a connection helper) to the class:

```csharp
    private static TectikaAgents.Core.Models.McpConnection Conn(string? defaultFrom = null) =>
        new() { CatalogId = "email", DefaultFrom = defaultFrom };

    [Fact]
    public async Task Uses_connection_default_from_when_arg_omitted()
    {
        var (conn, handler) = Build(Ok("{\"id\":\"x\"}"));
        var args = Args("{\"to\":\"x@y.com\",\"subject\":\"Hi\",\"body\":\"Hello\"}"); // no from
        var result = await conn.CallAsync("send_email", args, "re_k", Conn("Agents <a@acme.com>"), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("Agents <a@acme.com>", sent.RootElement.GetProperty("from").GetString());
        using var res = JsonDocument.Parse(result);
        Assert.Equal("sent", res.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Arg_from_overrides_connection_default()
    {
        var (conn, handler) = Build(Ok());
        var args = Args("{\"from\":\"b@acme.com\",\"to\":\"x@y.com\",\"subject\":\"Hi\",\"body\":\"Hello\"}");
        await conn.CallAsync("send_email", args, "re_k", Conn("a@acme.com"), CancellationToken.None);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("b@acme.com", sent.RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public async Task No_from_and_no_default_is_rejected_before_http()
    {
        var (conn, handler) = Build(Ok());
        var args = Args("{\"to\":\"x@y.com\",\"subject\":\"Hi\",\"body\":\"Hello\"}");
        var result = await conn.CallAsync("send_email", args, "re_k", Conn(null), CancellationToken.None);
        Assert.Contains("sender", result);
        Assert.Null(handler.Request);
    }
```

Also update the EXISTING connector tests that call `conn.CallAsync("send_email", …, "re_secret", CancellationToken.None)` to pass a connection: change each to `…, "re_secret", Conn("onboarding@resend.dev"), CancellationToken.None` (so the existing `from`-bearing args still send). There are calls in `Sends_post_to_resend_with_bearer_token_and_body`, `Non_2xx_returns_structured_error_and_never_leaks_token`, `Missing_required_field_is_rejected_before_any_http_call`, `Empty_body_is_rejected_before_any_http_call`, and `Unknown_tool_is_rejected`.

- [ ] **Step 2: Run tests to verify they fail to compile/fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~ResendEmail" --nologo -v q`
Expected: build error (signature) or FAIL — the connector doesn't yet apply `DefaultFrom`.

- [ ] **Step 3: Implement default-from in the connector**

In `ResendEmailConnector.cs` `CallAsync`, replace the field-extraction + guard block:

```csharp
        var from = Str(args, "from");
        var to = Str(args, "to");
        var subject = Str(args, "subject");
        var body = Str(args, "body");
        // All four are catalog-Required and we only ever send `text`, so enforce body here too (an empty
        // body would otherwise reach Resend as text:"" and 422 with a confusing remote error).
        if (from.Length == 0 || to.Length == 0 || subject.Length == 0 || body.Length == 0)
            return Err("send_email requires 'from', 'to', 'subject', and 'body'.");
```

with:

```csharp
        var to = Str(args, "to");
        var subject = Str(args, "subject");
        var body = Str(args, "body");
        // `from` is optional; fall back to the connection's configured default sender.
        var from = Str(args, "from");
        if (from.Length == 0) from = connection.DefaultFrom ?? string.Empty;

        if (from.Length == 0)
            return Err("No sender address is configured. Set a default From in Board Settings → Integrations → Email, or pass 'from'.");
        if (to.Length == 0 || subject.Length == 0 || body.Length == 0)
            return Err("send_email requires 'to', 'subject', and 'body'.");
```

- [ ] **Step 4: Make `from` optional in the catalog + bump version**

In `McpCatalog.cs`:
1. Change `Version` to `"mcp-catalog-v3"` (update the trailing comment to `// was v2 — send_email 'from' optional (defaults to the connection's From)`).
2. In the `send_email` tool, update the `from` property description and remove it from `Required`:

```csharp
                new("send_email", "Send an email through the connected Resend account.",
                    new Dictionary<string, TectikaToolSchema.ToolProp>
                    {
                        ["from"]    = new("string", "Optional sender override, e.g. 'Acme <noreply@yourdomain.com>'. Defaults to the board's configured sender; must be on a domain verified in the connected Resend account."),
                        ["to"]      = new("string", "Recipient email address."),
                        ["subject"] = new("string", "Email subject line."),
                        ["body"]    = new("string", "Plain-text body of the email."),
                    },
                    ["to", "subject", "body"], IsWrite: true),
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~ResendEmail|FullyQualifiedName~McpCatalog|FullyQualifiedName~TectikaToolSchema" --nologo -v q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/agentruntime/Mcp/ResendEmailConnector.cs src/agentruntime/Mcp/McpCatalog.cs tests/TectikaAgents.Tests/ResendEmailConnectorTests.cs
git commit -m "feat(agents): send_email from is optional, defaults to connection DefaultFrom"
```

---

## Task 4: `ResendDomainsClient` (typed Resend Domains API client)

**Files:**
- Create: `src/agentruntime/Mcp/ResendDomainsClient.cs`
- Create: `tests/TectikaAgents.Tests/ResendDomainsClientTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TectikaAgents.Tests/ResendDomainsClientTests.cs`:

```csharp
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.AgentRuntime.Mcp;
using Xunit;

public class ResendDomainsClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();
        public StubHandler(params HttpResponseMessage[] r) => _responses = new(r);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return _responses.Dequeue();
        }
    }
    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public StubFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }
    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    [Fact]
    public async Task Create_posts_name_with_bearer_and_parses_records()
    {
        var handler = new StubHandler(Json(HttpStatusCode.OK,
            "{\"id\":\"d1\",\"name\":\"acme.com\",\"status\":\"not_started\",\"records\":[{\"record\":\"DKIM\",\"name\":\"resend._domainkey\",\"type\":\"TXT\",\"ttl\":\"Auto\",\"value\":\"p=abc\"}]}"));
        var client = new ResendDomainsClient(new StubFactory(handler));

        var d = await client.CreateAsync("re_k", "acme.com", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://api.resend.com/domains", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("re_k", handler.Requests[0].Headers.Authorization!.Parameter);
        Assert.Contains("\"name\":\"acme.com\"", handler.Bodies[0]);
        Assert.Equal("d1", d.Id);
        Assert.Equal("not_started", d.Status);
        Assert.Single(d.Records);
        Assert.Equal("TXT", d.Records[0].Type);
        Assert.Equal("resend._domainkey", d.Records[0].Name);
    }

    [Fact]
    public async Task List_parses_data_array()
    {
        var handler = new StubHandler(Json(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"d1\",\"name\":\"acme.com\",\"status\":\"verified\"}]}"));
        var client = new ResendDomainsClient(new StubFactory(handler));
        var list = await client.ListAsync("re_k", CancellationToken.None);
        Assert.Single(list);
        Assert.Equal("verified", list[0].Status);
        Assert.Equal("https://api.resend.com/domains", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Verify_posts_to_verify_path()
    {
        var handler = new StubHandler(Json(HttpStatusCode.OK, "{\"object\":\"domain\",\"id\":\"d1\"}"));
        var client = new ResendDomainsClient(new StubFactory(handler));
        await client.VerifyAsync("re_k", "d1", CancellationToken.None);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://api.resend.com/domains/d1/verify", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Non_2xx_throws_with_status_and_no_token()
    {
        var handler = new StubHandler(Json(HttpStatusCode.UnprocessableEntity,
            "{\"name\":\"validation_error\",\"message\":\"Invalid domain\"}"));
        var client = new ResendDomainsClient(new StubFactory(handler));
        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => client.CreateAsync("re_secret", "bad", CancellationToken.None));
        Assert.Contains("422", ex.Message);
        Assert.DoesNotContain("re_secret", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~ResendDomainsClient" --nologo -v q`
Expected: build error — `ResendDomainsClient` doesn't exist.

- [ ] **Step 3: Implement the client + DTOs**

Create `src/agentruntime/Mcp/ResendDomainsClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TectikaAgents.AgentRuntime.Mcp;

public sealed record ResendDnsRecord(
    [property: JsonPropertyName("record")] string? Record,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ttl")] string? Ttl,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("priority")] int? Priority);

public sealed record ResendDomain(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("records")] IReadOnlyList<ResendDnsRecord>? Records);

/// <summary>Where + how to manage a Resend account's sending domains. The API key is passed per call and
/// never logged. Faked in tests; real impl below.</summary>
public interface IResendDomainsClient
{
    Task<IReadOnlyList<ResendDomain>> ListAsync(string apiKey, CancellationToken ct);
    Task<ResendDomain> CreateAsync(string apiKey, string name, CancellationToken ct);
    Task<ResendDomain> GetAsync(string apiKey, string id, CancellationToken ct);
    Task VerifyAsync(string apiKey, string id, CancellationToken ct);
    Task DeleteAsync(string apiKey, string id, CancellationToken ct);
}

public sealed class ResendDomainsClient : IResendDomainsClient
{
    public const string DomainsEndpoint = "https://api.resend.com/domains";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpFactory;
    public ResendDomainsClient(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    private sealed record ListResponse([property: JsonPropertyName("data")] List<ResendDomain>? Data);

    public async Task<IReadOnlyList<ResendDomain>> ListAsync(string apiKey, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, DomainsEndpoint, apiKey, null, ct);
        return JsonSerializer.Deserialize<ListResponse>(body, Json)?.Data ?? new List<ResendDomain>();
    }

    public async Task<ResendDomain> CreateAsync(string apiKey, string name, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { name });
        var body = await SendAsync(HttpMethod.Post, DomainsEndpoint, apiKey, payload, ct);
        return JsonSerializer.Deserialize<ResendDomain>(body, Json)!;
    }

    public async Task<ResendDomain> GetAsync(string apiKey, string id, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, $"{DomainsEndpoint}/{id}", apiKey, null, ct);
        return JsonSerializer.Deserialize<ResendDomain>(body, Json)!;
    }

    public Task VerifyAsync(string apiKey, string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{DomainsEndpoint}/{id}/verify", apiKey, null, ct);

    public Task DeleteAsync(string apiKey, string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{DomainsEndpoint}/{id}", apiKey, null, ct);

    private async Task<string> SendAsync(HttpMethod method, string url, string apiKey, string? payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (payload is not null)
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            // Resend error bodies never echo the API key.
            throw new InvalidOperationException($"Resend domains API returned {(int)resp.StatusCode}: {(body.Length <= 300 ? body : body[..300])}");
        return body;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~ResendDomainsClient" --nologo -v q`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/Mcp/ResendDomainsClient.cs tests/TectikaAgents.Tests/ResendDomainsClientTests.cs
git commit -m "feat(agents): ResendDomainsClient — typed client for the Resend Domains API"
```

---

## Task 5: `BoardEmailController` + DI

**Files:**
- Create: `src/api/TectikaAgents.Api/Controllers/BoardEmailController.cs`
- Modify: `src/api/TectikaAgents.Api/Program.cs`
- Create: `tests/TectikaAgents.Tests/BoardEmailControllerTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/TectikaAgents.Tests/BoardEmailControllerTests.cs`:

```csharp
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

    private static (BoardEmailController ctrl, FakeCosmosForBoardMcp cosmos, FakeSecretProvider secrets, string boardId) Build(FakeDomains domains, bool withEmail = true)
    {
        var cosmos = new FakeCosmosForBoardMcp();
        var secrets = new FakeSecretProvider();
        var board = cosmos.CreateBoardAsync(new Board { TenantId = "t1", Name = "B", OwnerId = "eli" }).Result;
        if (withEmail)
        {
            secrets.Store["sec1"] = "re_key";
            board.McpConnections.Add(new McpConnection { CatalogId = "email", SecretName = "sec1", Status = McpConnectionStatus.Connected });
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
        var board = await cosmos.GetBoardAsync("t1", boardId, CancellationToken.None);
        Assert.Equal("Agents <a@acme.com>", board!.McpConnections.First(c => c.CatalogId == "email").DefaultFrom);
    }

    [Fact]
    public async Task Set_from_rejects_address_without_at()
    {
        var (ctrl, _, _, boardId) = Build(new FakeDomains());
        var res = await ctrl.SetFrom(boardId, new BoardEmailController.SetFromRequest("not-an-email"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(res);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardEmailController" --nologo -v q`
Expected: build error — `BoardEmailController` doesn't exist.

- [ ] **Step 3: Implement the controller**

Create `src/api/TectikaAgents.Api/Controllers/BoardEmailController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

/// <summary>Manage the board's Email (Resend) sending domains + default sender. Proxies live to the Resend
/// Domains API using the connection's Key Vault key (never sent to the client). Scoped to the board's single
/// Connected `email` connection.</summary>
[ApiController]
[Route("api/boards/{boardId}/email")]
[Authorize]
public class BoardEmailController : ControllerBase
{
    public sealed record CreateDomainRequest(string Name);
    public sealed record SetFromRequest(string From);

    private readonly ICosmosDbService _cosmos;
    private readonly ISecretProvider _secrets;
    private readonly IResendDomainsClient _domains;

    public BoardEmailController(ICosmosDbService cosmos, ISecretProvider secrets, IResendDomainsClient domains)
    {
        _cosmos = cosmos; _secrets = secrets; _domains = domains;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    private async Task<(Board board, McpConnection conn, string apiKey)?> ResolveAsync(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        var conn = board?.McpConnections.FirstOrDefault(c => c.CatalogId == "email" && c.Status == McpConnectionStatus.Connected);
        if (board is null || conn is null) return null;
        var apiKey = await _secrets.GetSecretAsync(conn.SecretName, ct);
        if (string.IsNullOrEmpty(apiKey)) return null;
        return (board, conn, apiKey);
    }

    private IActionResult NoEmail() =>
        NotFound(new { error = "EmailNotConnected", detail = "Connect the Email integration on this board first." });

    private IActionResult Upstream(System.Exception ex) =>
        StatusCode(StatusCodes.Status502BadGateway, new { error = "ResendFailed", detail = ex.Message });

    [HttpGet("domains")]
    public async Task<IActionResult> ListDomains(string boardId, CancellationToken ct)
    {
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { return Ok(await _domains.ListAsync(r.Value.apiKey, ct)); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpPost("domains")]
    public async Task<IActionResult> CreateDomain(string boardId, [FromBody] CreateDomainRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "InvalidDomain" });
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { return Ok(await _domains.CreateAsync(r.Value.apiKey, req.Name.Trim(), ct)); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpGet("domains/{domainId}")]
    public async Task<IActionResult> GetDomain(string boardId, string domainId, CancellationToken ct)
    {
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { return Ok(await _domains.GetAsync(r.Value.apiKey, domainId, ct)); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpPost("domains/{domainId}/verify")]
    public async Task<IActionResult> VerifyDomain(string boardId, string domainId, CancellationToken ct)
    {
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { await _domains.VerifyAsync(r.Value.apiKey, domainId, ct); return Ok(await _domains.GetAsync(r.Value.apiKey, domainId, ct)); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpDelete("domains/{domainId}")]
    public async Task<IActionResult> DeleteDomain(string boardId, string domainId, CancellationToken ct)
    {
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        try { await _domains.DeleteAsync(r.Value.apiKey, domainId, ct); return NoContent(); }
        catch (System.Exception ex) { return Upstream(ex); }
    }

    [HttpPut("from")]
    public async Task<IActionResult> SetFrom(string boardId, [FromBody] SetFromRequest req, CancellationToken ct)
    {
        var from = (req.From ?? string.Empty).Trim();
        if (!from.Contains('@')) return BadRequest(new { error = "InvalidFrom", detail = "Enter a valid email address." });
        var r = await ResolveAsync(boardId, ct);
        if (r is null) return NoEmail();
        r.Value.conn.DefaultFrom = from;
        await _cosmos.UpdateBoardAsync(r.Value.board, ct);
        return Ok(new { defaultFrom = from });
    }
}
```

- [ ] **Step 4: Register the client in DI**

In `src/api/TectikaAgents.Api/Program.cs`, right after the `ResendEmailConnector` registration line (`builder.Services.AddSingleton<…IFirstPartyConnector, …ResendEmailConnector>();`), add:

```csharp
builder.Services.AddSingleton<TectikaAgents.AgentRuntime.Mcp.IResendDomainsClient, TectikaAgents.AgentRuntime.Mcp.ResendDomainsClient>();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~BoardEmailController" --nologo -v q`
Expected: PASS (5 tests).

- [ ] **Step 6: Run the FULL suite to confirm nothing regressed**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --nologo -v q 2>&1 | tail -3`
Expected: `Passed!` with 0 failures.

- [ ] **Step 7: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/BoardEmailController.cs src/api/TectikaAgents.Api/Program.cs tests/TectikaAgents.Tests/BoardEmailControllerTests.cs
git commit -m "feat(api): BoardEmailController — Resend sending-domain management + default From"
```

---

## Task 6: Web API client + types

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`
- Modify: `src/web/tectika-board/src/lib/api.ts`
- Create: `src/web/tectika-board/src/lib/__tests__/email-api.test.ts`

- [ ] **Step 1: Add types**

In `src/web/tectika-board/src/lib/types.ts`, add:

```typescript
export interface ResendDnsRecord { record?: string; name: string; type: string; ttl?: string; status?: string; value: string; priority?: number; }
export interface ResendDomain { id: string; name: string; status: string; records?: ResendDnsRecord[]; }
```

And add an optional field to the existing `McpConnection` interface:

```typescript
  defaultFrom?: string;
```

- [ ] **Step 2: Write failing route test**

Create `src/web/tectika-board/src/lib/__tests__/email-api.test.ts` (mirrors `mcp-api.test.ts`):

```typescript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as nodeModule from 'node:module';

type ResolveCtx = Record<string, unknown>;
type ResolveResult = { url: string; format?: string | null };
type NextResolve = (specifier: string, context: ResolveCtx) => ResolveResult;
const registerHooks = (nodeModule as unknown as {
  registerHooks: (hooks: { resolve: (s: string, c: ResolveCtx, n: NextResolve) => ResolveResult }) => void;
}).registerHooks;
registerHooks({
  resolve(specifier: string, context: ResolveCtx, nextResolve: NextResolve): ResolveResult {
    if (/^\.\.?\//.test(specifier) && !/\.[a-z]+$/i.test(specifier)) {
      try { return nextResolve(specifier + '.ts', context); } catch { return nextResolve(specifier, context); }
    }
    return nextResolve(specifier, context);
  },
});

const { api } = await import('../api.ts');

interface Call { url: string; method: string; body?: string }
function stubFetch(): { calls: Call[]; restore: () => void } {
  const calls: Call[] = [];
  const orig = globalThis.fetch;
  globalThis.fetch = (async (url: RequestInfo | URL, init: RequestInit = {}) => {
    calls.push({ url: String(url), method: init.method ?? 'GET', body: init.body as string | undefined });
    return new Response(JSON.stringify({ id: 'd1', name: 'acme.com', status: 'not_started' }), { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof globalThis.fetch;
  return { calls, restore: () => { globalThis.fetch = orig; } };
}

test('email client builds correct routes/methods/bodies', async () => {
  const { calls, restore } = stubFetch();
  try {
    await api.email.domains('b1');
    await api.email.addDomain('b1', 'acme.com');
    await api.email.getDomain('b1', 'd1');
    await api.email.verifyDomain('b1', 'd1');
    await api.email.deleteDomain('b1', 'd1');
    await api.email.setFrom('b1', 'a@acme.com');

    assert.ok(calls[0].url.endsWith('/api/boards/b1/email/domains')); assert.equal(calls[0].method, 'GET');
    assert.ok(calls[1].url.endsWith('/api/boards/b1/email/domains')); assert.equal(calls[1].method, 'POST');
    assert.match(calls[1].body!, /"name":"acme.com"/);
    assert.ok(calls[2].url.endsWith('/api/boards/b1/email/domains/d1')); assert.equal(calls[2].method, 'GET');
    assert.ok(calls[3].url.endsWith('/api/boards/b1/email/domains/d1/verify')); assert.equal(calls[3].method, 'POST');
    assert.ok(calls[4].url.endsWith('/api/boards/b1/email/domains/d1')); assert.equal(calls[4].method, 'DELETE');
    assert.ok(calls[5].url.endsWith('/api/boards/b1/email/from')); assert.equal(calls[5].method, 'PUT');
    assert.match(calls[5].body!, /"from":"a@acme.com"/);
  } finally { restore(); }
});
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `cd src/web/tectika-board && node --test --experimental-transform-types src/lib/__tests__/email-api.test.ts`
Expected: FAIL — `api.email` is undefined.

- [ ] **Step 4: Add the `api.email` client**

The request helper is `fetchApi<T>(path, options?)`. The `mcp:` group ends at `api.ts:217` (`},`) followed by a blank line and `models:`. Insert this `email:` group immediately after the `mcp:` group's closing `},` (i.e. before `models:`):

```typescript
  email: {
    domains: (boardId: string) =>
      fetchApi<ResendDomain[]>(`/api/boards/${boardId}/email/domains`),
    addDomain: (boardId: string, name: string) =>
      fetchApi<ResendDomain>(`/api/boards/${boardId}/email/domains`, { method: 'POST', body: JSON.stringify({ name }) }),
    getDomain: (boardId: string, id: string) =>
      fetchApi<ResendDomain>(`/api/boards/${boardId}/email/domains/${id}`),
    verifyDomain: (boardId: string, id: string) =>
      fetchApi<ResendDomain>(`/api/boards/${boardId}/email/domains/${id}/verify`, { method: 'POST' }),
    deleteDomain: (boardId: string, id: string) =>
      fetchApi<void>(`/api/boards/${boardId}/email/domains/${id}`, { method: 'DELETE' }),
    setFrom: (boardId: string, from: string) =>
      fetchApi<{ defaultFrom: string }>(`/api/boards/${boardId}/email/from`, { method: 'PUT', body: JSON.stringify({ from }) }),
  },
```

Then add `ResendDomain` to the existing per-symbol type import block at the top of `api.ts` — change the line `  McpConnection, McpCatalogEntry,` to `  McpConnection, McpCatalogEntry, ResendDomain,`.

- [ ] **Step 5: Run the test to verify it passes**

Run: `cd src/web/tectika-board && node --test --experimental-transform-types src/lib/__tests__/email-api.test.ts`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/lib/api.ts src/web/tectika-board/src/lib/__tests__/email-api.test.ts
git commit -m "feat(web): api.email client + Resend domain types"
```

---

## Task 7: Web UI — Sending domains panel

**Files:**
- Create: `src/web/tectika-board/src/components/board/settings/EmailDomainsPanel.tsx`
- Modify: `src/web/tectika-board/src/components/board/settings/McpIntegrationsTab.tsx`

- [ ] **Step 1: Read the existing tab + an existing settings component for patterns**

Run: `sed -n '1,90p' src/web/tectika-board/src/components/board/settings/McpIntegrationsTab.tsx`
Read `McpConnectModal.tsx` too — reuse its `Button`, `toast`, and CSS-var styling conventions.

**Important (web):** This is a *modified* Next.js (see `src/web/tectika-board/AGENTS.md`) — do NOT introduce unfamiliar Next APIs. The panel is a plain client component (`'use client'` + `useState`/`useEffect`), exactly like `McpConnectModal.tsx` and `McpIntegrationsTab.tsx` already are. Mirror those files' imports and conventions rather than inventing new ones; if `toast`/`Button` signatures differ from the snippet below, match what those files import.

- [ ] **Step 2: Create the panel**

Create `src/web/tectika-board/src/components/board/settings/EmailDomainsPanel.tsx`:

```tsx
'use client';

import { useEffect, useState } from 'react';
import { Button } from '@/components/ui/primitives';
import { api, ApiError } from '@/lib/api';
import type { ResendDomain } from '@/lib/types';
import { toast } from '@/lib/toast';

const VERIFIED = new Set(['verified']);
const FAILED = new Set(['failed', 'partially_failed', 'temporary_failure']);

function badge(status: string) {
  const color = VERIFIED.has(status) ? '#00c875' : FAILED.has(status) ? '#e2445c' : '#fdab3d';
  return <span className="text-[11px] font-semibold" style={{ color }}>● {status}</span>;
}

export function EmailDomainsPanel({ boardId, defaultFrom }: { boardId: string; defaultFrom?: string }) {
  const [domains, setDomains] = useState<ResendDomain[]>([]);
  const [name, setName] = useState('');
  const [from, setFrom] = useState(defaultFrom ?? '');
  const [busy, setBusy] = useState(false);

  const load = async () => {
    try { setDomains(await api.email.domains(boardId)); }
    catch (err) { if (err instanceof ApiError) toast('Could not load domains', 'error'); }
  };
  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [boardId]);

  const add = async () => {
    if (!name.trim()) return;
    setBusy(true);
    try { const d = await api.email.addDomain(boardId, name.trim()); setName(''); setDomains(p => [...p.filter(x => x.id !== d.id), d]); }
    catch { toast('Could not add domain', 'error'); }
    finally { setBusy(false); }
  };

  const verify = async (id: string) => {
    try { const d = await api.email.verifyDomain(boardId, id); setDomains(p => p.map(x => x.id === id ? d : x)); toast('Verification started — refresh in a minute', 'info'); }
    catch { toast('Could not verify', 'error'); }
  };

  const remove = async (id: string) => {
    try { await api.email.deleteDomain(boardId, id); setDomains(p => p.filter(x => x.id !== id)); }
    catch { toast('Could not remove', 'error'); }
  };

  const saveFrom = async () => {
    try { await api.email.setFrom(boardId, from.trim()); toast('Default sender saved', 'success'); }
    catch { toast('Could not save sender', 'error'); }
  };

  return (
    <div className="flex flex-col gap-4 mt-3">
      <div>
        <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Sending domains</span>
        <div className="flex gap-2 mt-1">
          <input className="inp flex-1" placeholder="yourdomain.com" value={name} onChange={e => setName(e.target.value)} />
          <Button onClick={add} disabled={busy || !name.trim()}>Add</Button>
        </div>
      </div>

      {domains.map(d => (
        <div key={d.id} className="rounded-lg border border-[var(--border)] p-3">
          <div className="flex items-center justify-between">
            <span className="font-semibold text-[13px]">{d.name}</span>
            <span className="flex items-center gap-3">{badge(d.status)}
              {!VERIFIED.has(d.status) && <button className="text-[12px] underline" onClick={() => verify(d.id)}>Verify</button>}
              <button className="text-[12px] text-[var(--muted)] underline" onClick={() => remove(d.id)}>Remove</button>
            </span>
          </div>
          {!VERIFIED.has(d.status) && d.records?.length ? (
            <div className="mt-2">
              <p className="text-[11px] text-[var(--muted)] mb-1">Add these DNS records at your domain host, then click Verify:</p>
              <div className="text-[11px] font-mono overflow-x-auto">
                {d.records.map((r, i) => (
                  <div key={i} className="grid grid-cols-[60px_1fr_2fr] gap-2 py-0.5 border-t border-[var(--border)]">
                    <span>{r.type}</span><span className="truncate">{r.name}</span><span className="truncate">{r.value}</span>
                  </div>
                ))}
              </div>
            </div>
          ) : null}
        </div>
      ))}

      <div>
        <span className="text-[11px] uppercase tracking-wide text-[var(--muted)] font-semibold">Default sender (From)</span>
        <div className="flex gap-2 mt-1">
          <input className="inp flex-1" placeholder="Agents <agents@yourdomain.com>" value={from} onChange={e => setFrom(e.target.value)} />
          <Button onClick={saveFrom} disabled={!from.trim()}>Save</Button>
        </div>
        <p className="text-[11px] text-[var(--muted)] mt-1">Agents send from this address unless they specify another on a verified domain.</p>
      </div>
    </div>
  );
}
```

If `@/components/ui/primitives` doesn't export `Button` or `@/lib/toast`'s `toast` signature differs, match what `McpConnectModal.tsx` imports (you read it in Step 1).

- [ ] **Step 3: Mount the panel under a connected Email connection**

`McpIntegrationsTab.tsx` renders `catalog.map(entry => { const conn = connections.find(c => c.catalogId === entry.id); … return (<div key={entry.id}>…</div>); })`. Add the import near the other component imports:

```tsx
import { EmailDomainsPanel } from './EmailDomainsPanel';
```

Inside that entry block's returned `<div>` (after the row markup, before its closing `</div>`), mount the panel for a connected Email integration:

```tsx
{entry.id === 'email' && conn?.status === 'Connected' && (
  <EmailDomainsPanel boardId={boardId} defaultFrom={conn.defaultFrom} />
)}
```

`boardId` is already a prop of `McpIntegrationsTab`; `entry` and `conn` are the loop locals shown above.

- [ ] **Step 4: Type-check / build the web app**

Run: `cd src/web/tectika-board && npx tsc --noEmit`
Expected: no errors in the new/edited files. (If the repo uses `npm run build`, run that instead and expect success.)

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/components/board/settings/EmailDomainsPanel.tsx src/web/tectika-board/src/components/board/settings/McpIntegrationsTab.tsx
git commit -m "feat(web): Sending domains panel (add/verify/DNS records) + default From on Email connection"
```

---

## Task 8: Final verification

- [ ] **Step 1: Full .NET suite**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --nologo -v q 2>&1 | tail -3`
Expected: `Passed!`, 0 failed.

- [ ] **Step 2: Web tests + build**

Run: `cd src/web/tectika-board && node --test --experimental-transform-types src/lib/__tests__/email-api.test.ts && npm run build`
Expected: test PASS, build succeeds.

- [ ] **Step 3: Confirm catalog version bump propagated**

Run: `grep -n "mcp-catalog-v" src/agentruntime/Mcp/McpCatalog.cs`
Expected: `mcp-catalog-v3` (so agents with email enabled republish with the new optional-`from` tool schema).

---

## Self-Review notes (spec coverage)

- Add domain + DNS records → Task 4/5/7. Verify (async + poll) → Task 5 (`VerifyDomain` returns fresh status) + Task 7 (Verify button + refresh-on-load). List/Get/Delete → Task 5/7. Default `from` storage → Task 1 + Task 5 (`SetFrom`). `send_email` default-from → Task 3. Live-proxy / no state stored → Task 4/5 (no Cosmos domain writes). Security (key server-side) → Task 5 (`ResolveAsync` reads KV; responses carry no key). Tests → Tasks 3-6. No new infra → nothing added. UI panel → Task 7.
