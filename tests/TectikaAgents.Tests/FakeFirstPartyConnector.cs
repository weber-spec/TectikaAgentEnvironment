using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.AgentRuntime.Mcp;

/// <summary>In-memory IFirstPartyConnector for tests: records the last call/token, lets a test force a
/// validation failure, and returns a canned tool result.</summary>
public sealed class FakeFirstPartyConnector : IFirstPartyConnector
{
    public string CatalogId { get; init; } = "email";
    public string? LastTool { get; private set; }
    public string? LastToken { get; private set; }
    public string? ValidatedToken { get; private set; }
    public bool ThrowOnValidate { get; set; }
    public string Result { get; set; } = "{\"status\":\"sent\"}";

    public Task ValidateAsync(string token, CancellationToken ct)
    {
        ValidatedToken = token;
        if (ThrowOnValidate) throw new InvalidOperationException("invalid key");
        return Task.CompletedTask;
    }

    public Task<string> CallAsync(string toolName, JsonElement args, string token, CancellationToken ct)
    {
        LastTool = toolName; LastToken = token;
        return Task.FromResult(Result);
    }
}
