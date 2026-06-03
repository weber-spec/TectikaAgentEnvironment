using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>
/// קורא ל-Azure OpenAI Chat Completions מתוך Durable Function Activities.
/// זהה לlogic ב-FoundryAgentService ב-API, אבל עצמאי (אין reference לAPI project).
/// </summary>
public class WorkflowAgentRunner
{
    private readonly HttpClient _http;
    private readonly FoundrySettings _foundry;
    private readonly DefaultAzureCredential? _credential;
    private readonly ILogger<WorkflowAgentRunner> _logger;

    public WorkflowAgentRunner(HttpClient http, IOptions<FoundrySettings> foundry, ILogger<WorkflowAgentRunner> logger)
    {
        _http = http;
        _foundry = foundry.Value;
        _logger = logger;
        if (string.IsNullOrEmpty(_foundry.ApiKey))
            _credential = new DefaultAzureCredential();
    }

    public async Task<AgentInvocationResult> InvokeAsync(
        AgentRole role,
        AgentTask task,
        List<Artifact> upstreamArtifacts,
        CancellationToken ct = default)
    {
        var model = role.ModelOverride ?? _foundry.DefaultModel;
        var messages = BuildMessages(role, task, upstreamArtifacts);

        string url, bearerToken;

        if (!string.IsNullOrEmpty(_foundry.ApiKey))
        {
            bearerToken = _foundry.ApiKey;
            url = _foundry.IsOpenAiDirect
                ? "https://api.openai.com/v1/chat/completions"
                : $"{_foundry.Endpoint.TrimEnd('/')}/openai/deployments/{model}/chat/completions?api-version=2024-05-01-preview";
        }
        else
        {
            var tokenCtx = new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]);
            var token = await _credential!.GetTokenAsync(tokenCtx, ct);
            bearerToken = token.Token;
            url = $"{_foundry.Endpoint.TrimEnd('/')}/openai/deployments/{model}/chat/completions?api-version=2024-05-01-preview";
        }

        var bodyObj = _foundry.IsOpenAiDirect
            ? (object)new { model, messages, max_tokens = 4096, temperature = 0.3 }
            : new { messages, max_tokens = 4096, temperature = 0.3 };
        var body = JsonSerializer.Serialize(bodyObj);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Invoking agent {RoleId} for task {TaskId}", role.Id, task.Id);

        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct);
        var completion = JsonSerializer.Deserialize<OpenAiCompletion>(json)
            ?? throw new Exception("Empty response from Azure OpenAI");

        var content = completion.Choices?.FirstOrDefault()?.Message?.Content
            ?? throw new Exception("No content in Azure OpenAI response");

        return new AgentInvocationResult(
            CompletionId: completion.Id ?? Guid.NewGuid().ToString(),
            Content: content,
            ContentType: DetectType(content, role),
            InputTokens: completion.Usage?.PromptTokens ?? 0,
            OutputTokens: completion.Usage?.CompletionTokens ?? 0);
    }

    private static List<object> BuildMessages(AgentRole role, AgentTask task, List<Artifact> upstream)
    {
        var msgs = new List<object> { new { role = "system", content = role.SystemPrompt } };

        var sb = new StringBuilder();
        sb.AppendLine($"## Task: {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description))
            sb.AppendLine(task.Description);

        foreach (var art in upstream)
        {
            sb.AppendLine();
            sb.AppendLine($"### Input ({art.ContentType}):");
            sb.AppendLine("```");
            sb.AppendLine(art.Content);
            sb.AppendLine("```");
        }

        sb.AppendLine("\nComplete the task. Be thorough and production-ready.");
        msgs.Add(new { role = "user", content = sb.ToString() });
        return msgs;
    }

    private static ArtifactContentType DetectType(string content, AgentRole role)
    {
        if (role.Id.Contains("backend") || role.Id.Contains("devops") || role.Id.Contains("qa"))
            return ArtifactContentType.Code;
        if (content.TrimStart().StartsWith('{') || content.TrimStart().StartsWith('['))
            return ArtifactContentType.Json;
        return ArtifactContentType.Markdown;
    }

    // ── Response DTOs ─────────────────────────────────────────────────────────

    private sealed class OpenAiCompletion
    {
        [JsonPropertyName("id")]      public string? Id { get; set; }
        [JsonPropertyName("choices")] public List<OpenAiChoice>? Choices { get; set; }
        [JsonPropertyName("usage")]   public OpenAiUsage? Usage { get; set; }
    }

    private sealed class OpenAiChoice
    {
        [JsonPropertyName("message")] public OpenAiMessage? Message { get; set; }
    }

    private sealed class OpenAiMessage
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    private sealed class OpenAiUsage
    {
        [JsonPropertyName("prompt_tokens")]     public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    }
}

public record AgentInvocationResult(
    string CompletionId,
    string Content,
    ArtifactContentType ContentType,
    int InputTokens,
    int OutputTokens);
