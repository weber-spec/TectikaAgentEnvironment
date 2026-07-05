using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.AgentRuntime.Mcp;
using Xunit;

public class SlackConnectorTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
    private static TectikaAgents.Core.Models.Connection Conn() => new() { CatalogId = "slack" };

    private static (SlackConnector conn, StubHandler handler) Build(string body, HttpStatusCode code = HttpStatusCode.OK)
    {
        var handler = new StubHandler(new HttpResponseMessage(code) { Content = new StringContent(body) });
        return (new SlackConnector(new StubHttpClientFactory(handler)), handler);
    }

    [Fact]
    public void CatalogId_is_slack() => Assert.Equal("slack", new SlackConnector(new StubHttpClientFactory(new StubHandler(new HttpResponseMessage()))).CatalogId);

    [Fact]
    public async Task Validate_accepts_ok_true()
    {
        var (conn, handler) = Build("{\"ok\":true,\"team\":\"Acme\"}");
        await conn.ValidateAsync("xoxb-good", CancellationToken.None); // must not throw
        Assert.Equal("https://slack.com/api/auth.test", handler.Request!.RequestUri!.ToString());
        Assert.Equal("xoxb-good", handler.Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Validate_rejects_ok_false()
    {
        var (conn, _) = Build("{\"ok\":false,\"error\":\"invalid_auth\"}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => conn.ValidateAsync("xoxb-bad", CancellationToken.None));
        Assert.Contains("invalid_auth", ex.Message);
    }

    [Fact]
    public async Task Post_message_sends_bearer_and_body_and_returns_sent()
    {
        var (conn, handler) = Build("{\"ok\":true,\"channel\":\"C1\",\"ts\":\"1700000000.000100\"}");
        var result = await conn.CallAsync("post_message", Args("{\"channel\":\"#general\",\"text\":\"hi\"}"), "xoxb-k", Conn(), CancellationToken.None);

        Assert.Equal("https://slack.com/api/chat.postMessage", handler.Request!.RequestUri!.ToString());
        Assert.Equal("xoxb-k", handler.Request.Headers.Authorization!.Parameter);
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("#general", sent.RootElement.GetProperty("channel").GetString());
        Assert.Equal("hi", sent.RootElement.GetProperty("text").GetString());

        using var res = JsonDocument.Parse(result);
        Assert.Equal("sent", res.RootElement.GetProperty("status").GetString());
        Assert.Equal("C1", res.RootElement.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Post_message_error_is_surfaced()
    {
        var (conn, _) = Build("{\"ok\":false,\"error\":\"channel_not_found\"}");
        var result = await conn.CallAsync("post_message", Args("{\"channel\":\"#nope\",\"text\":\"hi\"}"), "xoxb-k", Conn(), CancellationToken.None);
        using var res = JsonDocument.Parse(result);
        Assert.Contains("channel_not_found", res.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_message_missing_fields_rejected_before_http()
    {
        var (conn, handler) = Build("{\"ok\":true}");
        var result = await conn.CallAsync("post_message", Args("{\"channel\":\"#g\"}"), "xoxb-k", Conn(), CancellationToken.None); // no text
        Assert.Contains("requires", result);
        Assert.Null(handler.Request); // never hit the network
    }

    [Fact]
    public async Task List_channels_returns_id_and_name()
    {
        var (conn, _) = Build("{\"ok\":true,\"channels\":[{\"id\":\"C1\",\"name\":\"general\"},{\"id\":\"C2\",\"name\":\"random\"}]}");
        var result = await conn.CallAsync("list_channels", Args("{}"), "xoxb-k", Conn(), CancellationToken.None);
        using var res = JsonDocument.Parse(result);
        var channels = res.RootElement.GetProperty("channels");
        Assert.Equal(2, channels.GetArrayLength());
        Assert.Equal("general", channels[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unknown_tool_rejected()
    {
        var (conn, handler) = Build("{\"ok\":true}");
        var result = await conn.CallAsync("delete_workspace", Args("{}"), "xoxb-k", Conn(), CancellationToken.None);
        Assert.Contains("Unknown Slack tool", result);
        Assert.Null(handler.Request);
    }

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
