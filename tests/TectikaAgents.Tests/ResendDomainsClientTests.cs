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
