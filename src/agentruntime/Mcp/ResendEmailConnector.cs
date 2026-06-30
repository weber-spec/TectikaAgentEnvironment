using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>First-party connector for the "email" catalog integration, backed by the Resend REST API
/// (POST https://api.resend.com/emails). The board's Resend API key is sent as a bearer token per call and
/// is never logged; tool output is additionally scrubbed/capped by RoundExecutor before re-entering the model.</summary>
public sealed class ResendEmailConnector : IFirstPartyConnector
{
    public const string ResendEndpoint = "https://api.resend.com/emails";
    public const string ResendDomainsEndpoint = "https://api.resend.com/domains";

    private readonly IHttpClientFactory _httpFactory;

    public ResendEmailConnector(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    public string CatalogId => "email";

    /// <summary>Validate the Resend API key at connect time without sending mail, via an authenticated read.
    /// Resend's real contract on a read endpoint: 2xx = valid full-access key; 401 <c>restricted_api_key</c> =
    /// valid send-only key; 403 <c>invalid_api_key</c> = wrong key; 401 <c>missing_api_key</c> = blank/absent key.
    /// Accept only the first two and reject everything else, so an invalid key is never stored. THROWS on reject.</summary>
    public async Task ValidateAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ResendDomainsEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var resp = await http.SendAsync(req, ct);

        if (resp.IsSuccessStatusCode)
            return; // valid full-access key
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (body.Contains("restricted", StringComparison.OrdinalIgnoreCase))
                return; // valid send-only key (Resend 401s read endpoints for restricted keys)
        }
        // 403 invalid_api_key, 401 missing_api_key, and any other non-2xx → invalid; do not store.
        throw new InvalidOperationException($"Resend rejected the API key (HTTP {(int)resp.StatusCode}).");
    }

    public async Task<string> CallAsync(string toolName, JsonElement args, string token,
        TectikaAgents.Core.Models.McpConnection connection, CancellationToken ct)
    {
        if (toolName != "send_email")
            return Err($"Unknown email tool '{toolName}'.");

        var from = Str(args, "from");
        var to = Str(args, "to");
        var subject = Str(args, "subject");
        var body = Str(args, "body");
        // All four are catalog-Required and we only ever send `text`, so enforce body here too (an empty
        // body would otherwise reach Resend as text:"" and 422 with a confusing remote error).
        if (from.Length == 0 || to.Length == 0 || subject.Length == 0 || body.Length == 0)
            return Err("send_email requires 'from', 'to', 'subject', and 'body'.");

        var payload = JsonSerializer.Serialize(new { from, to, subject, text = body });

        using var req = new HttpRequestMessage(HttpMethod.Post, ResendEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var resp = await http.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            // Resend's error body (e.g. {"name":"validation_error","message":"..."}) never echoes the API key.
            return Err($"Resend returned {(int)resp.StatusCode}: {Trim(respBody)}");

        return JsonSerializer.Serialize(new { status = "sent", id = TryGetId(respBody), to });
    }

    private static string? TryGetId(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("id", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Err(string msg) => JsonSerializer.Serialize(new { error = msg });
}
