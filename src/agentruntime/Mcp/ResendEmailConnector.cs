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

    private readonly IHttpClientFactory _httpFactory;

    public ResendEmailConnector(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    public string CatalogId => "email";

    public async Task<string> CallAsync(string toolName, JsonElement args, string token, CancellationToken ct)
    {
        if (toolName != "send_email")
            return Err($"Unknown email tool '{toolName}'.");

        var from = Str(args, "from");
        var to = Str(args, "to");
        var subject = Str(args, "subject");
        var body = Str(args, "body");
        if (from.Length == 0 || to.Length == 0 || subject.Length == 0)
            return Err("send_email requires 'from', 'to', and 'subject'.");

        // Resend requires at least one of text/html/react; we send plain text.
        var payload = JsonSerializer.Serialize(new { from, to, subject, text = body });

        using var req = new HttpRequestMessage(HttpMethod.Post, ResendEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var http = _httpFactory.CreateClient();
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
