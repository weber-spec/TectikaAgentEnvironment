using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Live Claude model catalog: fetches the account's available models from Anthropic's
/// <c>GET /v1/models</c> using the connection's stored API key (or OAuth token). Caches per connection for a
/// short window. Unlike <see cref="FoundryModelCatalog"/> it NEVER throws — on OAuth-only connections
/// (where the models endpoint may reject the token) or any error it returns a curated fallback so the picker
/// always has a usable list.</summary>
public sealed class ClaudeModelCatalog : IClaudeModelCatalog
{
    private const string ModelsUrl = "https://api.anthropic.com/v1/models?limit=1000";
    private const string AnthropicVersion = "2023-06-01";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Shown when the live list can't be fetched (OAuth connection or API error). Kept small and
    /// current; the frontend also carries a fallback, but this keeps the API contract "always usable".</summary>
    public static readonly IReadOnlyList<string> CuratedFallback = new[]
    {
        "claude-fable-5", "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5",
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<ClaudeModelCatalog> _logger;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Models, DateTimeOffset At)> _cache = new();

    public ClaudeModelCatalog(IHttpClientFactory httpFactory, ISecretProvider secrets,
        ILogger<ClaudeModelCatalog> logger, Func<DateTimeOffset>? now = null)
    {
        _httpFactory = httpFactory;
        _secrets = secrets;
        _logger = logger;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(Connection conn, CancellationToken ct = default)
    {
        if (TryGetCached(conn.Id, out var cached)) return cached;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCached(conn.Id, out cached)) return cached;   // double-check after acquiring the gate
            var models = await FetchAsync(conn, ct).ConfigureAwait(false);
            _cache[conn.Id] = (models, _now());
            return models;
        }
        finally { _gate.Release(); }
    }

    private bool TryGetCached(string connId, out IReadOnlyList<string> models)
    {
        if (_cache.TryGetValue(connId, out var e) && _now() - e.At < CacheTtl) { models = e.Models; return true; }
        models = Array.Empty<string>();
        return false;
    }

    /// <summary>Read the connection's credential, call <c>/v1/models</c> with the right auth for the mode, and
    /// map to model ids. Any failure (missing secret, OAuth-unsupported, non-2xx, network) returns the curated
    /// fallback rather than throwing.</summary>
    private async Task<IReadOnlyList<string>> FetchAsync(Connection conn, CancellationToken ct)
    {
        string credential;
        try { credential = await _secrets.GetSecretAsync(conn.SecretName, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClaudeModelCatalog] could not read secret for connection {Conn}", conn.Id);
            return CuratedFallback;
        }
        if (string.IsNullOrWhiteSpace(credential))
            return CuratedFallback;

        // ApiKey → x-api-key (pay-as-you-go). OAuthToken (Pro/Max, from `claude setup-token`) uses a Bearer +
        // the oauth beta header; the models endpoint may still reject it, in which case we fall back.
        var mode = Enum.TryParse<ClaudeAuthMode>(conn.Metadata.GetValueOrDefault("claudeAuth"), ignoreCase: true, out var m)
            ? m : ClaudeAuthMode.ApiKey;

        try
        {
            var http = _httpFactory.CreateClient();
            using var reqMsg = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
            reqMsg.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            if (mode == ClaudeAuthMode.OAuthToken)
            {
                reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                reqMsg.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            }
            else
            {
                reqMsg.Headers.TryAddWithoutValidation("x-api-key", credential);
            }

            using var resp = await http.SendAsync(reqMsg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[ClaudeModelCatalog] /v1/models returned {Status} for connection {Conn} (mode={Mode}) — using fallback",
                    (int)resp.StatusCode, conn.Id, mode);
                return CuratedFallback;
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var ids = ParseModelIds(body);
            return ids.Count > 0 ? ids : CuratedFallback;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClaudeModelCatalog] fetch failed for connection {Conn} — using fallback", conn.Id);
            return CuratedFallback;
        }
    }

    /// <summary>Pure mapping from the <c>/v1/models</c> envelope to Claude model ids: keeps <c>data[].id</c>
    /// values that look like Claude models, de-duplicated, preserving Anthropic's (newest-first) order.
    /// Static + tolerant so it can be unit-tested in isolation (mirrors FoundryModelMapping.SelectChatModels).</summary>
    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString();
                if (string.IsNullOrEmpty(id) || !id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(id)) result.Add(id);
            }
        }
        catch (JsonException) { /* malformed → empty; the caller degrades to the curated fallback */ }
        return result;
    }
}
