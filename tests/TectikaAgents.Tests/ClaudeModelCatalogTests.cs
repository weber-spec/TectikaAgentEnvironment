using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

public class ClaudeModelCatalogTests
{
    private const string ModelsJson = """
    {
      "data": [
        { "type": "model", "id": "claude-fable-5",  "display_name": "Claude Fable 5" },
        { "type": "model", "id": "claude-opus-4-8", "display_name": "Claude Opus 4.8" },
        { "type": "model", "id": "gpt-4o",          "display_name": "not a claude model" }
      ],
      "has_more": false
    }
    """;

    // ── Pure mapping (ParseModelIds) ─────────────────────────────────────────────
    [Fact]
    public void ParseModelIds_KeepsClaudeIds_InOrder_DropsNonClaude()
    {
        var ids = ClaudeModelCatalog.ParseModelIds(ModelsJson);
        Assert.Equal(new[] { "claude-fable-5", "claude-opus-4-8" }, ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "data": "oops" }""")]
    [InlineData("""{ "data": [] }""")]
    public void ParseModelIds_ReturnsEmpty_OnMissingOrMalformedData(string json)
    {
        Assert.Empty(ClaudeModelCatalog.ParseModelIds(json));
    }

    // ── End-to-end behavior (auth headers, success, fallback) ────────────────────
    [Fact]
    public async Task ListModelsAsync_ApiKeyMode_SendsXApiKey_AndReturnsLiveIds()
    {
        var handler = new StubHandler(ModelsJson);
        var catalog = NewCatalog(handler, secret: "sk-ant-123");

        var models = await catalog.ListModelsAsync(NewConnection(ClaudeAuthMode.ApiKey));

        Assert.Equal(new[] { "claude-fable-5", "claude-opus-4-8" }, models);
        Assert.Contains("api.anthropic.com/v1/models", handler.LastRequestUri!.AbsoluteUri);
        Assert.True(handler.LastHeaders!.TryGetValues("x-api-key", out var key));
        Assert.Equal("sk-ant-123", Assert.Single(key!));
        Assert.False(handler.LastHeaders!.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task ListModelsAsync_OAuthMode_SendsBearer_AndOauthBetaHeader()
    {
        var handler = new StubHandler(ModelsJson);
        var catalog = NewCatalog(handler, secret: "oauth-token");

        await catalog.ListModelsAsync(NewConnection(ClaudeAuthMode.OAuthToken));

        Assert.Equal("Bearer", handler.LastAuthScheme);
        Assert.Equal("oauth-token", handler.LastAuthParameter);
        Assert.True(handler.LastHeaders!.TryGetValues("anthropic-beta", out var beta));
        Assert.Equal("oauth-2025-04-20", Assert.Single(beta!));
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsCuratedFallback_OnNonSuccess()
    {
        var handler = new StubHandler("nope", HttpStatusCode.Unauthorized);
        var catalog = NewCatalog(handler, secret: "sk-ant-123");

        var models = await catalog.ListModelsAsync(NewConnection(ClaudeAuthMode.ApiKey));

        Assert.Equal(ClaudeModelCatalog.CuratedFallback, models);
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsCuratedFallback_OnEmptySecret_WithoutCallingApi()
    {
        var handler = new StubHandler(ModelsJson);
        var catalog = NewCatalog(handler, secret: "");

        var models = await catalog.ListModelsAsync(NewConnection(ClaudeAuthMode.ApiKey));

        Assert.Equal(ClaudeModelCatalog.CuratedFallback, models);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ListModelsAsync_CachesPerConnection_WithinTtl_ThenRefetches()
    {
        var handler = new StubHandler(ModelsJson);
        var now = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
        var catalog = NewCatalog(handler, secret: "sk-ant-123", now: () => now);
        var conn = NewConnection(ClaudeAuthMode.ApiKey);

        await catalog.ListModelsAsync(conn);
        await catalog.ListModelsAsync(conn);      // within TTL → cached
        Assert.Equal(1, handler.Calls);

        now = now.AddMinutes(10);                 // past the 5-min TTL
        await catalog.ListModelsAsync(conn);
        Assert.Equal(2, handler.Calls);
    }

    private static ClaudeModelCatalog NewCatalog(StubHandler handler, string secret, Func<DateTimeOffset>? now = null) =>
        new(new StubHttpClientFactory(handler), new StubSecretProvider(secret),
            NullLogger<ClaudeModelCatalog>.Instance, now);

    private static Connection NewConnection(ClaudeAuthMode mode) => new()
    {
        Id = "conn_abc",
        CatalogId = "anthropic",
        Category = "model",
        SecretName = "conn-abc",
        Metadata = new() { ["claudeAuth"] = mode.ToString() },
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public Uri? LastRequestUri;
        public System.Net.Http.Headers.HttpRequestHeaders? LastHeaders;
        public string? LastAuthScheme;
        public string? LastAuthParameter;
        public int Calls;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequestUri = request.RequestUri;
            LastHeaders = request.Headers;
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            LastAuthParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubSecretProvider(string secret) : ISecretProvider
    {
        public Task<string> GetSecretAsync(string name, CancellationToken ct) => Task.FromResult(secret);
        public Task SetSecretAsync(string name, string value, CancellationToken ct) => Task.CompletedTask;
    }
}
