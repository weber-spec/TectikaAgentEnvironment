using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Observability;

namespace TectikaAgents.AgentRuntime;

// ─────────────────────────────────────────────────────────────────────────────
// NEW Foundry agent REST contract (live-verified against proj-agentteam):
//   token scope https://ai.azure.com/.default ; base = FoundrySettings.ProjectEndpoint
//   create:   POST {base}/agents?api-version=v1   {name, definition:{kind:"prompt",model,instructions,description?}}  (409 if name exists)
//   version:  POST {base}/agents/{name}/versions?api-version=v1   {definition:{…}}
//   delete:   DELETE {base}/agents/{name}?api-version=v1
//   convo:    POST {base}/conversations?api-version=v1  {}  -> {id:"conv_…"}
//   run:      POST {base}/openai/v1/responses   {input, agent_reference:{name,type:"agent_reference"}, conversation?}
//             -> {id,status,output:[{type:"message",content:[{type:"output_text",text}]}],usage:{input_tokens,output_tokens},error?}
//   agent name == id (no asst_ guid); agents are versioned.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Real new-Foundry agent runtime + provisioner (raw REST). Persists the stable agent
/// name onto AgentRole.FoundryAgentId and the conversation id onto AgentTask.FoundryThreadId;
/// the caller saves them to Cosmos.</summary>
public sealed class FoundryAgentRuntime : IAgentRuntime, IAgentProvisioner
{
    private const string Api = "api-version=v1";
    private static readonly string[] Scopes = ["https://ai.azure.com/.default"];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly FoundrySettings _settings;
    private readonly ILogger<FoundryAgentRuntime> _logger;
    private readonly bool _logSensitive;
    private readonly TokenCredential _credential = new DefaultAzureCredential();
    private readonly string _base;

    /// <summary>Optional per-turn sink for the agent's output text (one event, non-streaming).</summary>
    public Action<string>? OnText { get; set; }
    public Action<string>? OnStatus { get; set; }

    public FoundryAgentRuntime(IHttpClientFactory httpFactory, IOptions<FoundrySettings> settings,
        IOptions<LoggingSettings> logging, ILogger<FoundryAgentRuntime> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _logger = logger;
        _logSensitive = logging.Value.LogSensitiveContent;
        _base = _settings.ProjectEndpoint.TrimEnd('/');
    }

    private async Task<HttpClient> ClientAsync(CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        return http;
    }

    public async Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default)
    {
        try
        {
            var model = role.ModelOverride ?? _settings.DefaultModel;
            _logger.LogInformation("[FoundryEnsureAgent] ensuring agent role={RoleId} model={Model}", role.Id, model);
            var hash = AgentInstructionsHash.Compute(role.SystemPrompt, model);
            var definition = new AgentDefinition("prompt", model, role.SystemPrompt, role.DisplayName);
            var http = await ClientAsync(ct).ConfigureAwait(false);

            // Stable agent id: reuse the stored one; mint a fresh random id only for a brand-new role.
            var name = string.IsNullOrEmpty(role.FoundryAgentId) ? FoundryAgentName.New() : role.FoundryAgentId!;

            // Self-heal: the stored agent may have been deleted out-of-band — check whether it exists.
            var getResp = await http.GetAsync($"{_base}/agents/{name}?{Api}", ct).ConfigureAwait(false);
            if (getResp.StatusCode is not System.Net.HttpStatusCode.OK and not System.Net.HttpStatusCode.NotFound)
                await EnsureOkAsync(getResp, ct).ConfigureAwait(false); // surface unexpected errors
            var exists = getResp.StatusCode == System.Net.HttpStatusCode.OK;

            if (!exists)
            {
                // Create (or recreate) the agent WITH this id, so the stored FoundryAgentId stays valid.
                var resp = await http.PostAsJsonAsync($"{_base}/agents?{Api}", new CreateAgentRequest(name, definition), Json, ct).ConfigureAwait(false);
                if (resp.StatusCode != System.Net.HttpStatusCode.Conflict) // 409 = created concurrently → fine
                    await EnsureOkAsync(resp, ct).ConfigureAwait(false);
            }
            else if (role.FoundryAgentHash != hash)
            {
                // Prompt/model changed → publish a new version of the same agent.
                var resp = await http.PostAsJsonAsync($"{_base}/agents/{name}/versions?{Api}", new NewVersionRequest(definition), Json, ct).ConfigureAwait(false);
                await EnsureOkAsync(resp, ct).ConfigureAwait(false);
            }

            role.FoundryAgentId = name;
            role.FoundryAgentHash = hash;
            var result = new AgentSyncResult(name, true);
            _logger.LogInformation("[FoundryEnsureAgent] agent role={RoleId} foundryId={FoundryId} synced={Synced}", role.Id, result.FoundryAgentId, result.Synced);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnsureAgent failed for role {Role}", role.Id);
            return new AgentSyncResult(role.FoundryAgentId, false, ex.Message);
        }
    }

    public async Task DeleteAgentAsync(string? foundryAgentId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(foundryAgentId)) return;
        _logger.LogInformation("[FoundryDeleteAgent] deleting foundry agent {FoundryId}", foundryAgentId);
        try
        {
            var http = await ClientAsync(ct).ConfigureAwait(false);
            await http.DeleteAsync($"{_base}/agents/{foundryAgentId}?{Api}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteAgent failed (ignored) for {Id}", foundryAgentId); }
    }

    public async Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(task.FoundryThreadId)) return task.FoundryThreadId!;
        _logger.LogInformation("[FoundryThread] ensuring thread for task {TaskId}", task.Id);
        var http = await ClientAsync(ct).ConfigureAwait(false);
        var resp = await http.PostAsJsonAsync($"{_base}/conversations?{Api}", new EmptyBody(), Json, ct).ConfigureAwait(false);
        await EnsureOkAsync(resp, ct).ConfigureAwait(false);
        var conv = await resp.Content.ReadFromJsonAsync<ConversationResponse>(Json, ct).ConfigureAwait(false)
                   ?? throw new Exception("Empty conversation response from Foundry.");
        task.FoundryThreadId = conv.Id;
        var threadId = task.FoundryThreadId!;
        _logger.LogInformation("[FoundryThread] thread {ThreadId} ready for task {TaskId}", threadId, task.Id);
        return threadId;
    }

    public async Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(req.Role.FoundryAgentId))
            return Fail(req, "Role has no Foundry agent — ensure the agent first.");
        var model = req.Role.ModelOverride ?? _settings.DefaultModel;
        _logger.LogInformation("[FoundryAgentInvoke] running turn agent={AgentId} thread={ThreadId} model={Model} prompt={Prompt}",
            req.Role.FoundryAgentId, req.ThreadId, model, SensitiveContent.Format(req.UserMessage, _logSensitive));
        try
        {
            var http = await ClientAsync(ct).ConfigureAwait(false);
            var body = new ResponsesRequest(
                req.UserMessage,
                new AgentRef(req.Role.FoundryAgentId!, "agent_reference"),
                string.IsNullOrEmpty(req.ThreadId) ? null : req.ThreadId,
                req.MaxCompletionTokens > 0 ? req.MaxCompletionTokens : null);
            var resp = await http.PostAsJsonAsync($"{_base}/openai/v1/responses", body, Json, ct).ConfigureAwait(false);
            await EnsureOkAsync(resp, ct).ConfigureAwait(false);
            var r = await resp.Content.ReadFromJsonAsync<ResponsesResult>(Json, ct).ConfigureAwait(false)
                    ?? throw new Exception("Empty response from Foundry.");

            OnStatus?.Invoke(r.Status ?? "");
            var content = ExtractText(r);
            var usage = new TokenUsage { Input = r.Usage?.InputTokens ?? 0, Output = r.Usage?.OutputTokens ?? 0 };
            if (!string.IsNullOrEmpty(content)) OnText?.Invoke(content);

            var outcome = r.Status switch
            {
                "completed" => new AgentRunOutcome(AgentRunStatus.Completed, content, DetectType(content, req.Role), usage, r.Id ?? ""),
                "incomplete" => new AgentRunOutcome(AgentRunStatus.BudgetExceeded, content, ArtifactContentType.Markdown, usage, r.Id ?? ""),
                _ => Fail(req, $"Foundry response status '{r.Status}': {r.Error?.Message}"),
            };
            _logger.LogInformation("[FoundryAgentInvoke] turn complete agent={AgentId} status={Status} tokens={Tokens} output={Output}",
                req.Role.FoundryAgentId, outcome.Status, outcome.TokenUsage.Input + outcome.TokenUsage.Output,
                SensitiveContent.Format(outcome.Content, _logSensitive));
            return outcome;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunTurn failed for role {Role} task {Task}", req.Role.Id, req.Task.Id);
            return Fail(req, ex.Message);
        }
    }

    private async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger.LogError("[FoundryAgentInvoke] Foundry call failed status={Status} body={Body}", (int)resp.StatusCode, bodyText);
        throw new HttpRequestException($"Foundry {(int)resp.StatusCode} {resp.ReasonPhrase}: {bodyText}");
    }

    private static string ExtractText(ResponsesResult r)
    {
        if (r.Output is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var item in r.Output)
            if (item.Type == "message" && item.Content is not null)
                foreach (var c in item.Content)
                    if (c.Type == "output_text" && c.Text is not null) sb.Append(c.Text);
        return sb.ToString();
    }

    private static AgentRunOutcome Fail(AgentRunRequest req, string error) =>
        new(AgentRunStatus.Failed, "", ArtifactContentType.Markdown, new TokenUsage(), $"run-{req.RunId}-{req.Step}", Error: error);

    private static ArtifactContentType DetectType(string content, AgentRole role)
    {
        if (role.Id.Contains("backend") || role.Id.Contains("devops") || role.Id.Contains("qa"))
            return ArtifactContentType.Code;
        var t = content.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('[')) return ArtifactContentType.Json;
        return ArtifactContentType.Markdown;
    }

    // ── REST DTOs ─────────────────────────────────────────────────────────────
    private sealed record EmptyBody();
    private sealed record AgentDefinition(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("instructions")] string Instructions,
        [property: JsonPropertyName("description")] string? Description);
    private sealed record CreateAgentRequest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("definition")] AgentDefinition Definition);
    private sealed record NewVersionRequest(
        [property: JsonPropertyName("definition")] AgentDefinition Definition);
    private sealed record ConversationResponse(
        [property: JsonPropertyName("id")] string Id);
    private sealed record AgentRef(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type);
    private sealed record ResponsesRequest(
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("agent_reference")] AgentRef AgentReference,
        [property: JsonPropertyName("conversation")] string? Conversation,
        [property: JsonPropertyName("max_output_tokens")] int? MaxOutputTokens);
    private sealed class ResponsesResult
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("output")] public List<OutputItem>? Output { get; set; }
        [JsonPropertyName("usage")] public UsageInfo? Usage { get; set; }
        [JsonPropertyName("error")] public ErrorInfo? Error { get; set; }
    }
    private sealed class OutputItem
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("content")] public List<ContentItem>? Content { get; set; }
    }
    private sealed class ContentItem
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
    private sealed class UsageInfo
    {
        [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    }
    private sealed class ErrorInfo
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
