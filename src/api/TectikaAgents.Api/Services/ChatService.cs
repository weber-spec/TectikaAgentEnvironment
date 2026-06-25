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

    /// <summary>Reset the agent's context: new conversation next run, cleared brief, and a transcript
    /// boundary (ChatClearedAt). Non-destructive — RunEvents are kept, just hidden by the UI.</summary>
    Task<bool> ClearAsync(string boardId, string taskId, CancellationToken ct = default);

    /// <summary>Terminate the task's active run (Durable orchestration) and mark it Cancelled.</summary>
    Task<bool> StopAsync(string boardId, string taskId, CancellationToken ct = default);

    /// <summary>Summarize the conversation into the brief, then reset (like /clear). Returns whether a
    /// summary was produced (false = fell back to a plain clear).</summary>
    Task<bool> CompactAsync(string boardId, string taskId, CancellationToken ct = default);
}

public class ChatService : IChatService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly DurableFunctionsSettings _settings;
    private readonly SseConnectionManager _sse;
    private readonly ILogger<ChatService> _logger;

    public ChatService(ICosmosDbService cosmos, IHttpClientFactory httpFactory,
        IOptions<DurableFunctionsSettings> settings, SseConnectionManager sse, ILogger<ChatService> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _sse = sse;
        _logger = logger;
    }

    public async Task<ChatResult?> SendAsync(string boardId, string taskId, string tenantId, string text, CancellationToken ct = default)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return null;

        var run = task.WorkflowRunId is null ? null : await _cosmos.GetRunAsync(taskId, task.WorkflowRunId, ct);
        var active = run is { Status: RunStatus.Running or RunStatus.AwaitingInteraction }
                     && !string.IsNullOrEmpty(run.DurableFunctionInstanceId);

        if (active)
        {
            await EchoUserMessageAsync(run!.Id, taskId, run.CurrentStep, text, ct);
            await PostAsync(BuildUrl($"{run.DurableFunctionInstanceId}/message"), new { Text = text }, ct);
            // If the run was paused on a steerable request, a free-typed reply answers it — resolve the
            // record so it leaves the chat card, the Approvals tab, and the notification list.
            if (run.Status == RunStatus.AwaitingInteraction)
                await ResolvePendingSteerableInteractionAsync(tenantId, taskId, text, ct);
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
            TenantId = tenantId, TaskId = taskId, BoardId = boardId, Status = RunStatus.Pending,
            PreviousTaskStatus = task.Status
        }, ct);

        task.WorkflowRunId = newRun.Id;
        task.Status = AgentTaskStatus.InProgress;
        // Stamp a session id on first run-start (only when absent — never re-bump).
        if (task.UsageSessionId is null)
            task.UsageSessionId = Guid.NewGuid().ToString();
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

    private async Task EchoUserMessageAsync(string runId, string taskId, int round, string text, CancellationToken ct)
    {
        var ev = await _cosmos.CreateRunEventAsync(new RunEvent
        {
            RunId = runId, TaskId = taskId, Round = round, Kind = RunEventKind.UserMessage, Title = text
        }, ct);
        // Push to everyone watching this run's SSE stream (same API instance) so other participants see
        // the message in real time; best-effort — a broadcast failure must never abort the send (the
        // message is already persisted, and the client's 4s poll is the backstop).
        try { await _sse.BroadcastAsync(AgentEvent.FromRunEvent(ev), ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Chat] failed to broadcast user message for run {RunId}", runId); }
    }

    private async Task ResolvePendingSteerableInteractionAsync(string tenantId, string taskId, string text, CancellationToken ct)
    {
        try
        {
            var pending = await _cosmos.GetPendingInteractionsAsync(tenantId, ct);
            var match = pending.FirstOrDefault(i =>
                i.TaskId == taskId && i.Status == InteractionStatus.Pending);
            if (match is null) return;

            match.Status = InteractionStatus.Responded;
            match.RespondedAt = DateTimeOffset.UtcNow;
            match.Answer = text;   // free-typed reply; Approval decision is left null (answered as text)
            await _cosmos.UpdateInteractionAsync(match, ct);
            _logger.LogInformation("[Chat] resolved steerable interaction {Id} via free-typed reply", match.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chat] failed to resolve pending steerable interaction for task {TaskId}", taskId);
        }
    }

    public async Task<bool> ClearAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return false;
        task.FoundryThreadId = null;
        task.TaskBrief = "";
        task.ChatClearedAt = DateTimeOffset.UtcNow;
        var newSessionId = Guid.NewGuid().ToString();
        task.UsageSessionId = newSessionId;          // new session: subsequent events accrue to a fresh bucket
        await _cosmos.UpdateTaskAsync(task, ct);
        await _cosmos.ResetTaskUsageSessionAsync(task.TenantId, taskId, newSessionId, ct);  // reset rollup currentSession bucket
        _logger.LogInformation("[Chat] cleared task {TaskId}", taskId);
        return true;
    }

    public async Task<bool> StopAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        var run = task?.WorkflowRunId is null ? null : await _cosmos.GetRunAsync(taskId, task.WorkflowRunId, ct);
        if (run?.DurableFunctionInstanceId is null) return false;          // nothing running
        await PostAsync(BuildUrl($"{run.DurableFunctionInstanceId}/terminate"), new { }, ct);
        run.Status = RunStatus.Cancelled;
        await _cosmos.UpdateRunAsync(run, ct);
        // The terminated orchestration never runs its terminal status activity, so set the task here.
        // Restore the status it had before this run started (e.g. a Done task stays Done); fall back to
        // Backlog when unknown (older runs) — there is no Cancelled task status.
        task!.Status = run.PreviousTaskStatus ?? AgentTaskStatus.Backlog;
        await _cosmos.UpdateTaskAsync(task, ct);
        _logger.LogInformation("[Chat] stopped run {RunId} task {TaskId}", run.Id, taskId);
        return true;
    }

    public async Task<bool> CompactAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var res = await http.PostAsync(BuildUrl($"compact/{boardId}/{taskId}"), content, ct);
            if (!res.IsSuccessStatusCode) { _logger.LogError("[Chat] compact failed status={Status}", res.StatusCode); return false; }
            var data = JsonSerializer.Deserialize<JsonElement>(await res.Content.ReadAsStringAsync(ct));
            return data.TryGetProperty("summarized", out var s) && s.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) { _logger.LogError(ex, "[Chat] compact threw task {TaskId}", taskId); return false; }
    }

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
