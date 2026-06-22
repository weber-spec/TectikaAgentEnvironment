using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.AgentRuntime;

/// <summary>A model deployment as returned by Foundry's deployments listing. Only the fields we
/// need; the JSON binding is intentionally tolerant (see FoundryModelCatalog).</summary>
public sealed record FoundryDeployment(string? Name, string? Type, string? ModelName, string? ModelPublisher);

/// <summary>Pure mapping from raw Foundry deployments to the chat-model names shown in the picker.
/// Kept separate from the HTTP layer so the filtering rules are unit-tested in isolation.</summary>
public static class FoundryModelMapping
{
    // Name/model substrings that mark a non-chat deployment (embeddings, audio, image, moderation).
    // Conservative on purpose: a deployment we can't positively rule out stays in the list.
    private static readonly string[] NonChatMarkers =
        ["embedding", "embed", "whisper", "tts", "dall-e", "dalle", "moderation"];

    /// <summary>Chat-capable deployment names, de-duplicated, original order preserved, blanks dropped.</summary>
    public static IReadOnlyList<string> SelectChatModels(IEnumerable<FoundryDeployment> deployments)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var d in deployments)
        {
            var name = d.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (IsNonChat(name) || IsNonChat(d.ModelName)) continue;
            if (seen.Add(name)) result.Add(name);
        }
        return result;
    }

    private static bool IsNonChat(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var marker in NonChatMarkers)
            if (s.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>Real Foundry model catalog: enumerates the project's model deployments via the
/// data-plane REST endpoint (same base + credential as FoundryAgentRuntime) and returns the
/// chat-model names. Caches for a short window; propagates errors (no static fallback).</summary>
public sealed class FoundryModelCatalog : IModelCatalog
{
    private const string Api = "api-version=v1";
    private static readonly string[] Scopes = ["https://ai.azure.com/.default"];
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<FoundryModelCatalog> _logger;
    private readonly TokenCredential _credential;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _base;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string>? _cached;
    private DateTimeOffset _cachedAt;

    public FoundryModelCatalog(IHttpClientFactory httpFactory, IOptions<FoundrySettings> settings,
        ILogger<FoundryModelCatalog> logger, TokenCredential? credential = null, Func<DateTimeOffset>? now = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _credential = credential ?? new DefaultAzureCredential();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _base = settings.Value.ProjectEndpoint.TrimEnd('/');
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        if (TryGetCached(out var cached)) return cached;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCached(out cached)) return cached;   // double-check after acquiring the gate
            var models = await FetchAsync(ct).ConfigureAwait(false);
            _cached = models;
            _cachedAt = _now();
            return models;
        }
        finally { _gate.Release(); }
    }

    private bool TryGetCached(out IReadOnlyList<string> models)
    {
        if (_cached is not null && _now() - _cachedAt < CacheTtl) { models = _cached; return true; }
        models = [];
        return false;
    }

    private async Task<IReadOnlyList<string>> FetchAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        var url = $"{_base}/deployments?{Api}";
        _logger.LogInformation("[FoundryModelCatalog] listing deployments at {Url}", url);
        var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<DeploymentList>(Json, ct).ConfigureAwait(false);
        var deployments = payload?.Value ?? (IReadOnlyList<FoundryDeployment>)[];
        return FoundryModelMapping.SelectChatModels(deployments);
    }

    private sealed record DeploymentList(IReadOnlyList<FoundryDeployment>? Value);
}
