using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Core.Models;
using Xunit;

public class McpToolExecutorTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static (McpToolExecutor exec, FakeMcpGateway gw, FakeSecretProvider secrets) Build()
    {
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider();
        return (new McpToolExecutor(gw, secrets), gw, secrets);
    }

    private static List<McpConnection> SlackConn(string secretName = "s1") => new()
    {
        new McpConnection { CatalogId = "slack", SecretName = secretName, Status = McpConnectionStatus.Connected }
    };

    private static List<McpConnection> EmailConn(string secretName = "e1") => new()
    {
        new McpConnection { CatalogId = "email", SecretName = secretName, Status = McpConnectionStatus.Connected }
    };

    private const string EmailArgs =
        "{\"from\":\"onboarding@resend.dev\",\"to\":\"x@y.com\",\"subject\":\"Hi\",\"body\":\"yo\"}";

    private static (McpToolExecutor exec, FakeMcpGateway gw, FakeFirstPartyConnector conn, FakeSecretProvider secrets) BuildWithEmail()
    {
        var gw = new FakeMcpGateway();
        var secrets = new FakeSecretProvider();
        var conn = new FakeFirstPartyConnector();
        return (new McpToolExecutor(gw, secrets, new[] { conn }), gw, conn, secrets);
    }

    [Fact]
    public void CanHandle_only_known_catalog_tools()
    {
        var (exec, _, _) = Build();
        Assert.True(exec.CanHandle("slack__list_channels"));
        Assert.True(exec.CanHandle("slack__post_message"));
        Assert.False(exec.CanHandle("slack__not_a_tool"));
        Assert.False(exec.CanHandle("read_file"));
    }

    [Fact]
    public async Task Read_tool_calls_gateway_with_resolved_token()
    {
        var (exec, gw, secrets) = Build();
        secrets.Store["s1"] = "xoxb-abc";
        var role = new AgentRole { McpServers = { "slack" } };

        var result = await exec.ExecuteAsync("slack__list_channels", Args("{}"), SlackConn(), role, CancellationToken.None);

        Assert.Equal("list_channels", gw.LastTool);
        Assert.Equal("xoxb-abc", gw.LastTarget!.Token);
        Assert.Equal("Bearer", gw.LastTarget!.AuthScheme);
        Assert.Equal(gw.Result, result);
    }

    [Fact]
    public async Task No_connection_returns_friendly_error_and_does_not_call_gateway()
    {
        var (exec, gw, _) = Build();
        var role = new AgentRole { McpServers = { "slack" } };

        var result = await exec.ExecuteAsync("slack__list_channels", Args("{}"), new List<McpConnection>(), role, CancellationToken.None);

        Assert.Contains("not connected", result);
        Assert.Null(gw.LastTool);
    }

    [Fact]
    public async Task Write_tool_without_optin_is_refused()
    {
        var (exec, gw, secrets) = Build();
        secrets.Store["s1"] = "xoxb-abc";
        var role = new AgentRole { McpServers = { "slack" } }; // no McpWriteEnabled

        var result = await exec.ExecuteAsync("slack__post_message",
            Args("{\"channel\":\"#x\",\"text\":\"hi\"}"), SlackConn(), role, CancellationToken.None);

        Assert.Contains("not permitted", result);
        Assert.Null(gw.LastTool);
    }

    [Fact]
    public async Task Write_tool_with_optin_calls_gateway()
    {
        var (exec, gw, secrets) = Build();
        secrets.Store["s1"] = "xoxb-abc";
        var role = new AgentRole { McpServers = { "slack" }, McpWriteEnabled = { "slack" } };

        await exec.ExecuteAsync("slack__post_message",
            Args("{\"channel\":\"#x\",\"text\":\"hi\"}"), SlackConn(), role, CancellationToken.None);

        Assert.Equal("post_message", gw.LastTool);
    }

    [Fact]
    public async Task Gateway_exception_becomes_structured_error()
    {
        var secrets = new FakeSecretProvider();
        secrets.Store["s1"] = "xoxb-abc";
        var exec = new McpToolExecutor(new ThrowingGateway(), secrets);
        var role = new AgentRole { McpServers = { "slack" } };

        var result = await exec.ExecuteAsync("slack__list_channels", Args("{}"), SlackConn(), role, CancellationToken.None);
        Assert.Contains("error", result);
    }

    // ── First-party (Email/Resend) routing ────────────────────────────────────

    [Fact]
    public void CanHandle_includes_first_party_email_tools()
    {
        var (exec, _, _, _) = BuildWithEmail();
        Assert.True(exec.CanHandle("email__send_email"));
        Assert.False(exec.CanHandle("email__not_a_tool"));
    }

    [Fact]
    public async Task First_party_write_routes_to_connector_with_token_not_gateway()
    {
        var (exec, gw, conn, secrets) = BuildWithEmail();
        secrets.Store["e1"] = "re_key";
        var role = new AgentRole { McpServers = { "email" }, McpWriteEnabled = { "email" } };

        var result = await exec.ExecuteAsync("email__send_email", Args(EmailArgs), EmailConn(), role, CancellationToken.None);

        Assert.Equal("send_email", conn.LastTool);
        Assert.Equal("re_key", conn.LastToken);
        Assert.Null(gw.LastTool);            // gateway untouched for first-party entries
        Assert.Equal(conn.Result, result);
    }

    [Fact]
    public async Task First_party_write_without_optin_is_refused_and_connector_not_called()
    {
        var (exec, _, conn, secrets) = BuildWithEmail();
        secrets.Store["e1"] = "re_key";
        var role = new AgentRole { McpServers = { "email" } }; // no write opt-in

        var result = await exec.ExecuteAsync("email__send_email", Args(EmailArgs), EmailConn(), role, CancellationToken.None);

        Assert.Contains("not permitted", result);
        Assert.Null(conn.LastTool);
    }

    [Fact]
    public async Task First_party_no_connection_returns_friendly_error_and_skips_connector()
    {
        var (exec, _, conn, _) = BuildWithEmail();
        var role = new AgentRole { McpServers = { "email" }, McpWriteEnabled = { "email" } };

        var result = await exec.ExecuteAsync("email__send_email", Args(EmailArgs), new List<McpConnection>(), role, CancellationToken.None);

        Assert.Contains("not connected", result);
        Assert.Null(conn.LastTool);
    }

    [Fact]
    public async Task First_party_entry_with_no_registered_connector_is_unavailable()
    {
        var secrets = new FakeSecretProvider();
        secrets.Store["e1"] = "re_key";
        var exec = new McpToolExecutor(new FakeMcpGateway(), secrets); // no connectors registered
        var role = new AgentRole { McpServers = { "email" }, McpWriteEnabled = { "email" } };

        var result = await exec.ExecuteAsync("email__send_email", Args(EmailArgs), EmailConn(), role, CancellationToken.None);

        Assert.Contains("unavailable", result);
    }

    private sealed class FakeFirstPartyConnector : TectikaAgents.AgentRuntime.Mcp.IFirstPartyConnector
    {
        public string CatalogId => "email";
        public string? LastTool { get; private set; }
        public string? LastToken { get; private set; }
        public string Result { get; set; } = "{\"status\":\"sent\"}";

        public Task<string> CallAsync(string toolName, JsonElement args, string token, CancellationToken ct)
        {
            LastTool = toolName; LastToken = token;
            return Task.FromResult(Result);
        }
    }

    private sealed class ThrowingGateway : TectikaAgents.Core.Interfaces.IMcpGateway
    {
        public Task<IReadOnlyList<TectikaAgents.Core.Interfaces.McpToolInfo>> ListToolsAsync(TectikaAgents.Core.Interfaces.McpServerTarget t, CancellationToken ct) => throw new System.Exception("boom");
        public Task<string> CallToolAsync(TectikaAgents.Core.Interfaces.McpServerTarget t, string n, string a, CancellationToken ct) => throw new System.Exception("boom");
    }
}
