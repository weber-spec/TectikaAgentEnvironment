using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>First-party connector for the "slack" catalog integration, backed by the Slack Web API directly
/// (auth.test / conversations.list / chat.postMessage) with the workspace's Bot User OAuth token (xoxb-…).
/// Mirrors <see cref="ResendEmailConnector"/> — no remote MCP server involved. Slack returns HTTP 200 even on
/// logical failures, carrying <c>{ "ok": false, "error": "…" }</c>, so every response is checked for <c>ok</c>.
/// The token is sent as a bearer per call and never logged.</summary>
public sealed class SlackConnector : IFirstPartyConnector
{
    public const string AuthTest = "https://slack.com/api/auth.test";
    public const string ConversationsList = "https://slack.com/api/conversations.list?limit=200&exclude_archived=true&types=public_channel";
    public const string PostMessage = "https://slack.com/api/chat.postMessage";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    public SlackConnector(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    public string CatalogId => "slack";

    /// <summary>Validate the bot token at connect time via auth.test (no side effects). THROWS if Slack rejects
    /// it (e.g. invalid_auth / account_inactive) so an invalid token is never stored.</summary>
    public async Task ValidateAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, AuthTest, token, content: null, ct);
        if (!IsOk(doc.RootElement, out var error))
            throw new InvalidOperationException($"Slack rejected the token ({error}).");
    }

    public async Task<string> CallAsync(string toolName, JsonElement args, string token,
        TectikaAgents.Core.Models.Connection connection, CancellationToken ct)
    {
        switch (toolName)
        {
            case "list_channels":
            {
                using var doc = await SendAsync(HttpMethod.Get, ConversationsList, token, content: null, ct);
                if (!IsOk(doc.RootElement, out var error)) return Err($"Slack list_channels failed: {error}");
                var channels = doc.RootElement.TryGetProperty("channels", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray()
                        .Select(c => new { id = Str(c, "id"), name = Str(c, "name") })
                        .Where(c => c.id.Length > 0)
                        .ToArray()
                    : [];
                return JsonSerializer.Serialize(new { channels });
            }
            case "post_message":
            {
                var channel = Str(args, "channel");
                var text = Str(args, "text");
                if (channel.Length == 0 || text.Length == 0)
                    return Err("post_message requires 'channel' and 'text'.");
                var payload = JsonSerializer.Serialize(new { channel, text });
                using var body = new StringContent(payload, Encoding.UTF8, "application/json");
                using var doc = await SendAsync(HttpMethod.Post, PostMessage, token, body, ct);
                if (!IsOk(doc.RootElement, out var error)) return Err($"Slack post_message failed: {error}");
                return JsonSerializer.Serialize(new { status = "sent", channel = Str(doc.RootElement, "channel"), ts = Str(doc.RootElement, "ts") });
            }
            default:
                return Err($"Unknown Slack tool '{toolName}'.");
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, string token, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        // Slack always returns JSON with an "ok" flag; a non-JSON body (e.g. a proxy error) is surfaced as not-ok.
        try { return JsonDocument.Parse(raw); }
        catch { return JsonDocument.Parse("{\"ok\":false,\"error\":\"non_json_response\"}"); }
    }

    private static bool IsOk(JsonElement root, out string error)
    {
        error = "";
        var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        if (!ok) error = Str(root, "error") is { Length: > 0 } e ? e : "unknown_error";
        return ok;
    }

    private static string Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });
}
