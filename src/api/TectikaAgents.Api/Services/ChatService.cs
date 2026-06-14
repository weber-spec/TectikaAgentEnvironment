using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public sealed record ChatResult(string RunId, bool Injected);

public interface IChatService
{
    /// <summary>Send a chat message to a task: start a steerable run seeded with the text if none is
    /// active, otherwise inject it as a steering message into the live run. Returns null if the task
    /// is not found or has no assigned agent.</summary>
    Task<ChatResult?> SendAsync(string boardId, string taskId, string tenantId, string text, CancellationToken ct = default);
}

public class ChatService : IChatService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly DurableFunctionsSettings _settings;
    private readonly ILogger<ChatService> _logger;

    public ChatService(ICosmosDbService cosmos, IHttpClientFactory httpFactory,
        IOptions<DurableFunctionsSettings> settings, ILogger<ChatService> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<ChatResult?> SendAsync(string boardId, string taskId, string tenantId, string text, CancellationToken ct = default)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return null;

        var run = task.WorkflowRunId is null ? null : await _cosmos.GetRunAsync(taskId, task.WorkflowRunId, ct);
        var active = run is { Status: RunStatus.Running or RunStatus.AwaitingInteraction or RunStatus.PausedApproval }
                     && !string.IsNullOrEmpty(run.DurableFunctionInstanceId);

        if (active)
        {
            await EchoUserMessageAsync(run!.Id, taskId, run.CurrentStep, text, ct);
            await PostAsync(BuildUrl($"{run.DurableFunctionInstanceId}/message"), new { Text = text }, ct);
            _logger.LogInformation("[Chat] injected message into run {RunId} task {TaskId}", run.Id, taskId);
            return new ChatResult(run.Id, Injected: true);
        }

        // No active run → start a steerable run seeded with the message.
        if (task.Assignee.Type != AssigneeType.Agent || string.IsNullOrEmpty(task.Assignee.Id))
        {
            _logger.LogWarning("[Chat] task {TaskId} has no assigned agent; cannot start a run", taskId);
            return null;
        }

        var newRun = await _cosmos.CreateRunAsync(new WorkflowRun
        {
            TenantId = tenantId, TaskId = taskId, Status = RunStatus.Pending
        }, ct);

        task.WorkflowRunId = newRun.Id;
        task.Status = AgentTaskStatus.InProgress;
        await _cosmos.UpdateTaskAsync(task, ct);

        await EchoUserMessageAsync(newRun.Id, taskId, 0, text, ct);

        var startInput = new
        {
            RunId = newRun.Id, TaskId = taskId, BoardId = boardId, TenantId = tenantId,
            AgentRoleId = task.Assignee.Id, SeedMessage = text
        };
        var instanceId = await StartAndReadInstanceAsync(BuildUrl("steerable/start"), startInput, ct);
        if (instanceId is not null)
        {
            newRun.DurableFunctionInstanceId = instanceId;
            await _cosmos.UpdateRunAsync(newRun, ct);
        }
        _logger.LogInformation("[Chat] started steerable run {RunId} task {TaskId} instance {Instance}", newRun.Id, taskId, instanceId);
        return new ChatResult(newRun.Id, Injected: false);
    }

    private Task EchoUserMessageAsync(string runId, string taskId, int round, string text, CancellationToken ct) =>
        _cosmos.CreateRunEventAsync(new RunEvent
        {
            RunId = runId, TaskId = taskId, Round = round, Kind = RunEventKind.UserMessage, Title = text
        }, ct);

    // DurableFunctionsSettings.StartUrl points at ".../api/pipelines/start"; derive sibling routes.
    private string BuildUrl(string suffix)
    {
        var baseUrl = _settings.StartUrl.EndsWith("/start", StringComparison.OrdinalIgnoreCase)
            ? _settings.StartUrl[..^"/start".Length]
            : _settings.StartUrl;
        var url = $"{baseUrl}/{suffix}";
        if (!string.IsNullOrEmpty(_settings.FunctionKey))
            url += $"?code={Uri.EscapeDataString(_settings.FunctionKey)}";
        return url;
    }

    private async Task PostAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var res = await http.PostAsync(url, content, ct);
            if (!res.IsSuccessStatusCode)
                _logger.LogError("[Chat] workflow call failed url={Url} status={Status}", url, res.StatusCode);
        }
        catch (Exception ex) { _logger.LogError(ex, "[Chat] workflow call threw url={Url}", url); }
    }

    private async Task<string?> StartAndReadInstanceAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var res = await http.PostAsync(url, content, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError("[Chat] steerable start failed status={Status}", res.StatusCode);
                return null;
            }
            var data = JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync(ct));
            return data.TryGetProperty("instanceId", out var id) ? id.GetString() : null;
        }
        catch (Exception ex) { _logger.LogError(ex, "[Chat] steerable start threw"); return null; }
    }
}
