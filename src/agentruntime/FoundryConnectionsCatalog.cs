using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;

namespace TectikaAgents.AgentRuntime;

/// <summary>One connection defined inside the Azure AI Foundry project (e.g. an Azure OpenAI, Azure AI Search,
/// or Storage connection). Tolerant binding — only the fields we surface.</summary>
public sealed record FoundryConnection(string? Name, string? Type, string? Target);

/// <summary>Lists the connections that live inside the Foundry project via the data-plane REST endpoint
/// (same base + credential as <see cref="FoundryAgentRuntime"/>). Short-cached; returns empty (never throws)
/// when Foundry isn't configured or the call fails, so the Connections → Foundry tab degrades gracefully.</summary>
public sealed class FoundryConnectionsCatalog
{
    private const string Api = "api-version=v1";
    private static readonly string[] Scopes = ["https://ai.azure.com/.default"];
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<FoundryConnectionsCatalog> _logger;
    private readonly TokenCredential _credential;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _base;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<FoundryConnection>? _cached;
    private DateTimeOffset _cachedAt;

    public FoundryConnectionsCatalog(IHttpClientFactory httpFactory, IOptions<FoundrySettings> settings,
        ILogger<FoundryConnectionsCatalog> logger, TokenCredential? credential = null, Func<DateTimeOffset>? now = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _credential = credential ?? new DefaultAzureCredential();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _base = settings.Value.ProjectEndpoint.TrimEnd('/');
    }

    public async Task<IReadOnlyList<FoundryConnection>> ListAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_base)) return [];
        if (TryGetCached(out var cached)) return cached;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out cached)) return cached;
            var list = await FetchAsync(ct).ConfigureAwait(false);
            _cached = list;
            _cachedAt = _now();
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FoundryConnectionsCatalog] listing connections failed — returning empty");
            return [];
        }
        finally { _gate.Release(); }
    }

    private bool TryGetCached(out IReadOnlyList<FoundryConnection> list)
    {
        if (_cached is not null && _now() - _cachedAt < CacheTtl) { list = _cached; return true; }
        list = [];
        return false;
    }

    private async Task<IReadOnlyList<FoundryConnection>> FetchAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        var url = $"{_base}/connections?{Api}";
        _logger.LogInformation("[FoundryConnectionsCatalog] listing connections at {Url}", url);
        var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<ConnectionList>(Json, ct).ConfigureAwait(false);
        return payload?.Value ?? (IReadOnlyList<FoundryConnection>)[];
    }

    private sealed record ConnectionList(IReadOnlyList<FoundryConnection>? Value);
}
