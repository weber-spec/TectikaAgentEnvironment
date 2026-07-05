using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.AgentRuntime.Mcp;
using Xunit;
using Xunit.Abstractions;

/// <summary>End-to-end check against the real Resend API. Skipped unless RESEND_API_KEY is set, so CI/offline
/// runs never make a network call. Uses Resend's no-verification test sender (onboarding@resend.dev) and test
/// sink (delivered@resend.dev), so it sends nothing to a real person.
/// Run: RESEND_API_KEY=re_... dotnet test --filter ResendEmailLiveTests</summary>
public class ResendEmailLiveTests
{
    private readonly ITestOutputHelper _out;
    public ResendEmailLiveTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Sends_a_real_email_via_resend()
    {
        var key = Environment.GetEnvironmentVariable("RESEND_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            _out.WriteLine("RESEND_API_KEY not set — skipping live Resend send.");
            return;
        }

        var to = Environment.GetEnvironmentVariable("RESEND_TEST_TO") ?? "delivered@resend.dev";
        var from = Environment.GetEnvironmentVariable("RESEND_TEST_FROM") ?? "onboarding@resend.dev";
        var connector = new ResendEmailConnector(new RealHttpClientFactory());
        var args = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            from,
            to,
            subject = "Tectika agent email — live test",
            body = "This message was sent by the Tectika Email integration end-to-end test.",
        })).RootElement;

        var result = await connector.CallAsync("send_email", args, key, new TectikaAgents.Core.Models.Connection { CatalogId = "email" }, CancellationToken.None);
        _out.WriteLine(result);

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.TryGetProperty("error", out _), $"Resend send failed: {result}");
        Assert.Equal("sent", doc.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
    }

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
