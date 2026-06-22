using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Configuration;
using Xunit;

public class FoundryModelCatalogTests
{
    private const string DeploymentsJson = """
    {
      "value": [
        { "name": "gpt-4o", "type": "ModelDeployment", "modelName": "gpt-4o", "modelPublisher": "OpenAI" },
        { "name": "text-embedding-3-large", "type": "ModelDeployment", "modelName": "text-embedding-3-large", "modelPublisher": "OpenAI" },
        { "name": "claude-opus-4-8", "type": "ModelDeployment", "modelName": "claude-opus-4-8", "modelPublisher": "Anthropic" }
      ]
    }
    """;

    [Fact]
    public async Task ListModelsAsync_FetchesProjectDeployments_MapsToChatModelNames()
    {
        var handler = new StubHandler(DeploymentsJson);
        var catalog = NewCatalog(handler);

        var models = await catalog.ListModelsAsync();

        Assert.Equal(new[] { "gpt-4o", "claude-opus-4-8" }, models);
        Assert.NotNull(handler.LastRequestUri);
        Assert.Contains("/deployments", handler.LastRequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ListModelsAsync_CachesWithinTtl_ThenRefetchesAfterExpiry()
    {
        var handler = new StubHandler(DeploymentsJson);
        var now = new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero);
        var catalog = NewCatalog(handler, () => now);

        await catalog.ListModelsAsync();
        await catalog.ListModelsAsync();          // within TTL → served from cache
        Assert.Equal(1, handler.Calls);

        now = now.AddMinutes(10);                 // past the 5-min TTL
        await catalog.ListModelsAsync();
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ListModelsAsync_PropagatesError_OnNonSuccessResponse()
    {
        var handler = new StubHandler("nope", HttpStatusCode.InternalServerError);
        var catalog = NewCatalog(handler);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => catalog.ListModelsAsync());
    }

    private static FoundryModelCatalog NewCatalog(StubHandler handler, Func<DateTimeOffset>? now = null)
    {
        var settings = Options.Create(new FoundrySettings
        {
            ProjectEndpoint = "https://sub.services.ai.azure.com/api/projects/proj",
        });
        return new FoundryModelCatalog(
            new StubHttpClientFactory(handler), settings,
            NullLogger<FoundryModelCatalog>.Instance, new FakeCredential(), now);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public Uri? LastRequestUri;
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

    private sealed class FakeCredential : TokenCredential
    {
        private static readonly AccessToken Token = new("fake-token", DateTimeOffset.MaxValue);
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct) => Token;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct) => new(Token);
    }
}
