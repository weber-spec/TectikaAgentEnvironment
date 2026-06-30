using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.AgentRuntime.Mcp;
using Xunit;

public class ResendEmailConnectorTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private const string ValidArgs =
        "{\"from\":\"onboarding@resend.dev\",\"to\":\"x@y.com\",\"subject\":\"Hi\",\"body\":\"Hello there\"}";

    private static (ResendEmailConnector conn, StubHandler handler) Build(HttpResponseMessage response)
    {
        var handler = new StubHandler(response);
        var conn = new ResendEmailConnector(new StubHttpClientFactory(handler));
        return (conn, handler);
    }

    [Fact]
    public void CatalogId_is_email() => Assert.Equal("email", new ResendEmailConnector(new StubHttpClientFactory(new StubHandler(Ok()))).CatalogId);

    [Fact]
    public async Task Sends_post_to_resend_with_bearer_token_and_body()
    {
        var (conn, handler) = Build(Ok("{\"id\":\"abc-123\"}"));

        var result = await conn.CallAsync("send_email", Args(ValidArgs), "re_secret", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://api.resend.com/emails", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("re_secret", handler.Request.Headers.Authorization!.Parameter);

        using var sent = JsonDocument.Parse(handler.RequestBody!);
        var root = sent.RootElement;
        Assert.Equal("onboarding@resend.dev", root.GetProperty("from").GetString());
        Assert.Equal("x@y.com", root.GetProperty("to").GetString());
        Assert.Equal("Hi", root.GetProperty("subject").GetString());
        Assert.Equal("Hello there", root.GetProperty("text").GetString());

        using var res = JsonDocument.Parse(result);
        Assert.Equal("sent", res.RootElement.GetProperty("status").GetString());
        Assert.Equal("abc-123", res.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Non_2xx_returns_structured_error_and_never_leaks_token()
    {
        var (conn, _) = Build(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"name\":\"validation_error\",\"message\":\"from is not verified\"}"),
        });

        var result = await conn.CallAsync("send_email", Args(ValidArgs), "re_secret", CancellationToken.None);

        using var res = JsonDocument.Parse(result);
        Assert.True(res.RootElement.TryGetProperty("error", out var err));
        Assert.Contains("422", err.GetString());
        Assert.DoesNotContain("re_secret", result);
    }

    [Fact]
    public async Task Missing_required_field_is_rejected_before_any_http_call()
    {
        var (conn, handler) = Build(Ok());

        var result = await conn.CallAsync("send_email",
            Args("{\"from\":\"a@b.com\",\"subject\":\"Hi\",\"body\":\"x\"}"), "re_secret", CancellationToken.None); // no 'to'

        Assert.Contains("requires", result);
        Assert.Null(handler.Request); // never reached the network
    }

    [Fact]
    public async Task Unknown_tool_is_rejected()
    {
        var (conn, handler) = Build(Ok());
        var result = await conn.CallAsync("delete_everything", Args("{}"), "re_secret", CancellationToken.None);
        Assert.Contains("Unknown email tool", result);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Validate_accepts_a_full_access_key_2xx()
    {
        var (conn, handler) = Build(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") });
        await conn.ValidateAsync("re_full", CancellationToken.None); // must not throw
        Assert.Equal("https://api.resend.com/domains", handler.Request!.RequestUri!.ToString());
        Assert.Equal("re_full", handler.Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Validate_accepts_a_restricted_send_only_key_401()
    {
        var (conn, _) = Build(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"name\":\"restricted_api_key\",\"message\":\"This API key is restricted to only send emails\"}"),
        });
        await conn.ValidateAsync("re_send_only", CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task Validate_rejects_an_invalid_key_401()
    {
        var (conn, _) = Build(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"name\":\"validation_error\",\"message\":\"API key is invalid\"}"),
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => conn.ValidateAsync("nope", CancellationToken.None));
    }

    private static HttpResponseMessage Ok(string body = "{\"id\":\"id-1\"}") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return _response;
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
