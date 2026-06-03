using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

/// <summary>
/// Foundry Agent Service integration — קורא ל-Azure OpenAI Chat Completions API
/// עם system prompt מה-AgentRole וcontext מ-upstream artifacts.
/// </summary>
public class FoundryAgentService
{
    private readonly HttpClient _http;
    private readonly FoundrySettings _foundry;
    private readonly ServiceBusSender? _eventsSender;
    private readonly ILogger<FoundryAgentService> _logger;
    private readonly DefaultAzureCredential? _credential;

    public FoundryAgentService(
        HttpClient http,
        IOptions<FoundrySettings> foundry,
        IOptions<ServiceBusSettings> serviceBus,
        ILogger<FoundryAgentService> logger)
    {
        _http = http;
        _foundry = foundry.Value;
        _logger = logger;

        // API key mode — no MSI needed
        if (string.IsNullOrEmpty(_foundry.ApiKey))
            _credential = new DefaultAzureCredential();

        if (!string.IsNullOrEmpty(serviceBus.Value.Namespace) &&
            !serviceBus.Value.Namespace.StartsWith("__", StringComparison.Ordinal))
        {
            var sbClient = _credential is not null
                ? new ServiceBusClient(serviceBus.Value.Namespace, _credential)
                : new ServiceBusClient(serviceBus.Value.Namespace);
            _eventsSender = sbClient.CreateSender(serviceBus.Value.AgentEventsTopic);
        }
    }

    /// <summary>
    /// מריץ agent על task, שולח events לService Bus, ומחזיר artifact.
    /// Azure OpenAI Chat Completions API — POST /openai/deployments/{model}/chat/completions
    /// </summary>
    public async Task<AgentRunResult> RunAgentAsync(
        AgentRole role,
        AgentTask task,
        IEnumerable<Artifact> upstreamArtifacts,
        string runId,
        int stepIndex,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var logs = new List<string>();

        await PublishEventAsync(new AgentEvent
        {
            Type = AgentEvent.Types.StepStarted,
            RunId = runId,
            TaskId = task.Id,
            Step = stepIndex,
            AgentRole = role.Id,
            Content = $"{role.DisplayName} started"
        }, ct);

        // ── 1. Build messages ────────────────────────────────────────────────
        var messages = BuildMessages(role, task, upstreamArtifacts);
        logs.Add($"Built context: {messages.Count} messages, {upstreamArtifacts.Count()} upstream artifacts");

        await PublishEventAsync(new AgentEvent
        {
            Type = AgentEvent.Types.AgentThinking,
            RunId = runId, TaskId = task.Id,
            Step = stepIndex, AgentRole = role.Id,
            Content = "Analyzing task..."
        }, ct);

        // ── 2. Build request URL + auth header ───────────────────────────────
        var model = role.ModelOverride ?? _foundry.DefaultModel;

        string url, bearerToken;

        if (!string.IsNullOrEmpty(_foundry.ApiKey))
        {
            // API Key mode — OpenAI direct OR Azure OpenAI with key
            bearerToken = _foundry.ApiKey;
            url = _foundry.IsOpenAiDirect
                ? $"https://api.openai.com/v1/chat/completions"
                : $"{_foundry.Endpoint.TrimEnd('/')}/openai/deployments/{model}/chat/completions?api-version=2024-05-01-preview";
        }
        else
        {
            // MSI / DefaultAzureCredential (Azure OpenAI with Managed Identity)
            var tokenCtx = new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]);
            var token = await _credential!.GetTokenAsync(tokenCtx, ct);
            bearerToken = token.Token;
            url = $"{_foundry.Endpoint.TrimEnd('/')}/openai/deployments/{model}/chat/completions?api-version=2024-05-01-preview";
        }

        var requestBody = _foundry.IsOpenAiDirect
            ? (object)new { model, messages, max_tokens = 4096, temperature = 0.3 }
            : new { messages, max_tokens = 4096, temperature = 0.3, stream = false };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Calling Azure OpenAI: model={Model}, task={TaskId}", model, task.Id);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure OpenAI call failed for role {Role}", role.Id);
            throw new Exception($"Azure OpenAI error: {ex.Message}", ex);
        }

        // ── 4. Parse response ────────────────────────────────────────────────
        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson)
            ?? throw new Exception("Failed to parse Azure OpenAI response.");

        var assistantContent = completion.Choices?.FirstOrDefault()?.Message?.Content
            ?? throw new Exception("Azure OpenAI returned empty content.");

        var usage = new TokenUsage
        {
            Input  = completion.Usage?.PromptTokens ?? 0,
            Output = completion.Usage?.CompletionTokens ?? 0
        };

        sw.Stop();
        logs.Add($"Completed in {sw.Elapsed.TotalSeconds:F1}s — {usage.Total} tokens");

        var contentType = DetectContentType(assistantContent, role);

        await PublishEventAsync(new AgentEvent
        {
            Type = AgentEvent.Types.StepCompleted,
            RunId = runId, TaskId = task.Id,
            Step = stepIndex, AgentRole = role.Id,
            TokenUsage = usage,
            Content = $"Done in {sw.Elapsed.TotalSeconds:F1}s"
        }, ct);

        return new AgentRunResult(
            FoundryRunId: completion.Id ?? Guid.NewGuid().ToString(),
            ArtifactContent: assistantContent,
            ContentType: contentType,
            TokenUsage: usage,
            InternalLogs: logs);
    }

    // ── Message builders ──────────────────────────────────────────────────────

    private static List<object> BuildMessages(AgentRole role, AgentTask task, IEnumerable<Artifact> upstream)
    {
        var messages = new List<object>
        {
            new { role = "system", content = role.SystemPrompt }
        };

        var sb = new StringBuilder();
        sb.AppendLine($"## Task: {task.Title}");

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            sb.AppendLine();
            sb.AppendLine(task.Description);
        }

        var artifacts = upstream.ToList();
        if (artifacts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Input from upstream agents:");
            foreach (var art in artifacts)
            {
                sb.AppendLine();
                sb.AppendLine($"### {art.ContentType} input:");
                sb.AppendLine("```");
                sb.AppendLine(art.Content);
                sb.AppendLine("```");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Complete the task above. Be thorough and production-ready.");

        messages.Add(new { role = "user", content = sb.ToString() });
        return messages;
    }

    private static ArtifactContentType DetectContentType(string content, AgentRole role)
    {
        if (role.Id.Contains("backend") || role.Id.Contains("devops") || role.Id.Contains("qa"))
            return ArtifactContentType.Code;
        if (content.TrimStart().StartsWith('{') || content.TrimStart().StartsWith('['))
            return ArtifactContentType.Json;
        return ArtifactContentType.Markdown;
    }

    private async Task PublishEventAsync(AgentEvent agentEvent, CancellationToken ct)
    {
        if (_eventsSender is null) return;
        try
        {
            var json = JsonSerializer.Serialize(agentEvent);
            await _eventsSender.SendMessageAsync(new ServiceBusMessage(json), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish agent event {Type}", agentEvent.Type);
        }
    }

    // ── OpenAI response DTOs ──────────────────────────────────────────────────

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("id")]    public string? Id { get; set; }
        [JsonPropertyName("choices")] public List<Choice>? Choices { get; set; }
        [JsonPropertyName("usage")]   public UsageDto? Usage { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public MessageDto? Message { get; set; }
    }

    private sealed class MessageDto
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    private sealed class UsageDto
    {
        [JsonPropertyName("prompt_tokens")]     public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    }
}

public record AgentRunResult(
    string FoundryRunId,
    string ArtifactContent,
    ArtifactContentType ContentType,
    TokenUsage TokenUsage,
    List<string> InternalLogs);
