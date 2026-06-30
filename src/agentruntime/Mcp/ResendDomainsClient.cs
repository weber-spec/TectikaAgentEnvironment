using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TectikaAgents.AgentRuntime.Mcp;

public sealed record ResendDnsRecord(
    [property: JsonPropertyName("record")] string? Record,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ttl")] string? Ttl,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("priority")] int? Priority);

public sealed record ResendDomain(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("records")] IReadOnlyList<ResendDnsRecord>? Records);

/// <summary>Where + how to manage a Resend account's sending domains. The API key is passed per call and
/// never logged. Faked in tests; real impl below.</summary>
public interface IResendDomainsClient
{
    Task<IReadOnlyList<ResendDomain>> ListAsync(string apiKey, CancellationToken ct);
    Task<ResendDomain> CreateAsync(string apiKey, string name, CancellationToken ct);
    Task<ResendDomain> GetAsync(string apiKey, string id, CancellationToken ct);
    Task VerifyAsync(string apiKey, string id, CancellationToken ct);
    Task DeleteAsync(string apiKey, string id, CancellationToken ct);
}

public sealed class ResendDomainsClient : IResendDomainsClient
{
    public const string DomainsEndpoint = "https://api.resend.com/domains";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpFactory;
    public ResendDomainsClient(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    private sealed record ListResponse([property: JsonPropertyName("data")] List<ResendDomain>? Data);

    public async Task<IReadOnlyList<ResendDomain>> ListAsync(string apiKey, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, DomainsEndpoint, apiKey, null, ct);
        return JsonSerializer.Deserialize<ListResponse>(body, Json)?.Data ?? new List<ResendDomain>();
    }

    public async Task<ResendDomain> CreateAsync(string apiKey, string name, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { name });
        var body = await SendAsync(HttpMethod.Post, DomainsEndpoint, apiKey, payload, ct);
        return JsonSerializer.Deserialize<ResendDomain>(body, Json)!;
    }

    public async Task<ResendDomain> GetAsync(string apiKey, string id, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, $"{DomainsEndpoint}/{id}", apiKey, null, ct);
        return JsonSerializer.Deserialize<ResendDomain>(body, Json)!;
    }

    public Task VerifyAsync(string apiKey, string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{DomainsEndpoint}/{id}/verify", apiKey, null, ct);

    public Task DeleteAsync(string apiKey, string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"{DomainsEndpoint}/{id}", apiKey, null, ct);

    private async Task<string> SendAsync(HttpMethod method, string url, string apiKey, string? payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (payload is not null)
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            // Resend error bodies never echo the API key.
            throw new InvalidOperationException($"Resend domains API returned {(int)resp.StatusCode}: {(body.Length <= 300 ? body : body[..300])}");
        return body;
    }
}
