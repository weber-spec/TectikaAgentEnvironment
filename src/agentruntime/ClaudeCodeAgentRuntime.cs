using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>
/// IAgentRuntime implementation that runs the <c>claude</c> CLI (Claude Code) inside the per-board ACI
/// workspace container. Unlike <see cref="FoundryAgentRuntime"/>, Claude Code owns its OWN internal
/// agentic tool-loop, so one "round" here == ONE <c>claude -p</c> (or <c>--resume</c>) invocation that
/// loops to completion and returns <see cref="RoundKind.Final"/>. The Durable orchestrator therefore
/// never drives fine-grained model⇄tool iteration for this engine.
///
/// The CLI is launched as a detached job via the executor's async <c>/job/*</c> endpoints (the
/// synchronous <c>/run</c> path is hard-capped at 300s, far below a real Claude run), and this method
/// polls for completion — safe because it executes inside a Durable *activity*, not the orchestrator.
/// </summary>
public sealed class ClaudeCodeAgentRuntime : IAgentRuntime
{
    /// <summary>Fallback model when the role has no explicit override (the UI normally sets a Claude id).</summary>
    private const string DefaultClaudeModel = "claude-opus-4-8";
    /// <summary>Runaway backstop — the analogue of Foundry's MaxToolRounds.</summary>
    private const int MaxTurns = 60;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    /// <summary>Strictly below host.json functionTimeout (00:30:00) so an over-long run returns a clean
    /// classified timeout instead of the Functions host killing the worker.</summary>
    private static readonly TimeSpan PollDeadline = TimeSpan.FromMinutes(25);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex ApiKeyPattern = new(@"sk-ant-[A-Za-z0-9_\-]+", RegexOptions.Compiled);

    private readonly IWorkspaceService _workspace;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<ClaudeCodeAgentRuntime> _logger;

    /// <summary>Optional per-turn sink for the agent's final text (one event, non-streaming) — mirrors Foundry.</summary>
    public Action<string>? OnText { get; set; }

    public ClaudeCodeAgentRuntime(IWorkspaceService workspace, ISecretProvider secrets,
        ILogger<ClaudeCodeAgentRuntime> logger)
    {
        _workspace = workspace;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Claude has no thread to create up front — its session id is only known AFTER the first
    /// invocation (returned in the result envelope). Return the stored session id (empty on first run);
    /// the activity persists the new id from the round outcome's CompletionId.</summary>
    public Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default)
        => Task.FromResult(task.ClaudeSessionId ?? "");

    /// <summary>Legacy multi-round turn path is unused for Claude Code (Claude owns its own loop).</summary>
    public Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, IProjectExplorer explorer, CancellationToken ct = default)
        => throw new NotSupportedException("ClaudeCodeAgentRuntime runs as single-round Shape B; RunTurnAsync is not used.");

    public async Task<RoundOutcome> RunRoundAsync(RoundRequest req, IProjectExplorer explorer, CancellationToken ct = default)
    {
        var id = $"round-{req.RunId}-{req.Round}";
        using var _ = _logger.BeginScope(new Dictionary<string, object>
        {
            ["runId"] = req.RunId, ["taskId"] = req.Task.Id, ["round"] = req.Round, ["engine"] = "claude-code"
        });

        // Claude Code requires the sandbox worktree to operate on.
        if (req.Workspace is null)
            return Fail(id, "Claude Code requires a workspace, but none was provided.", RunFailureClass.SandboxInfra);

        string apiKey;
        try
        {
            if (string.IsNullOrEmpty(req.Role.ApiKeySecretName))
                return Fail(id, "Claude Code role has no Anthropic API key configured.", RunFailureClass.ModelProvider);
            apiKey = await _secrets.GetSecretAsync(req.Role.ApiKeySecretName, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
                return Fail(id, "Anthropic API key secret was empty.", RunFailureClass.ModelProvider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCode] failed to read API key secret for role {Role}", req.Role.Id);
            return Fail(id, "Could not read the Anthropic API key from Key Vault.", RunFailureClass.SandboxInfra);
        }

        WorkspaceConnection? conn;
        try
        {
            conn = await req.Workspace.EnsureAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCode] workspace ensure threw run={RunId}", req.RunId);
            return Fail(id, req.Workspace.LastError ?? ex.Message, RunFailureClass.SandboxInfra);
        }
        if (conn is null || string.IsNullOrEmpty(conn.RunId))
            return Fail(id, req.Workspace.LastError ?? "The agent's secure workspace could not be started.", RunFailureClass.SandboxInfra);

        try
        {
            var model = string.IsNullOrWhiteSpace(req.Role.ModelOverride) ? DefaultClaudeModel : req.Role.ModelOverride!;
            var runIdShort = conn.RunId!;
            var jobId = $"{runIdShort}-r{req.Round}";
            var promptPath = $".claude-job/{jobId}.prompt";

            // Write the prompt to a file in the worktree so arbitrary content (newlines, quotes) never has
            // to survive shell quoting. `claude -p "$(cat …)"` then feeds it as a single safe argument.
            await _workspace.InvokeAsync(conn.Endpoint, conn.Token, "/write",
                new { path = promptPath, content = req.UserInput ?? "", run_id = runIdShort }, ct).ConfigureAwait(false);

            // Resume the existing session on continuation/steering rounds; fresh run otherwise. `--resume`
            // is best-effort: if the session is gone (container recycled), Claude errors and we fall back to
            // a fresh prompt on the next round (the activity rebuilds full context when ClaudeSessionId is null).
            var resume = string.IsNullOrEmpty(req.ThreadId) ? "" : $" --resume {req.ThreadId}";
            var cmd =
                $"mkdir -p .claude-job && claude -p \"$(cat {promptPath})\"" +
                $" --output-format json --model {model} --max-turns {MaxTurns} --dangerously-skip-permissions{resume}";

            _logger.LogInformation("[ClaudeCode] starting job {JobId} model={Model} resume={Resume}",
                jobId, model, !string.IsNullOrEmpty(req.ThreadId));

            // The API key is delivered per-invocation in the job env (NOT a container env var: the ACI is
            // per-board/shared, the key is per-agent). The executor injects it into the spawned process only.
            await _workspace.InvokeAsync(conn.Endpoint, conn.Token, "/job/start",
                new { cmd, run_id = runIdShort, job_id = jobId, env = new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = apiKey } },
                ct).ConfigureAwait(false);

            var resultJson = await PollJobAsync(conn, runIdShort, jobId, ct).ConfigureAwait(false);
            if (resultJson is null)
                return Fail(id, "Claude Code run exceeded the time limit.", RunFailureClass.Exhaustion);

            var (stdout, exitCode) = resultJson.Value;
            return ParseEnvelope(stdout, exitCode, id, apiKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCode] run failed role={Role} task={Task} round={Round}", req.Role.Id, req.Task.Id, req.Round);
            return Fail(id, Scrub(ex.Message, null), RunFailureClass.ModelProvider);
        }
    }

    /// <summary>Poll /job/status until the job exits or the deadline passes; then fetch /job/result.
    /// Returns (stdout, exitCode) or null on deadline. Safe in a Durable activity (real IO + delays).</summary>
    private async Task<(string Stdout, int ExitCode)?> PollJobAsync(WorkspaceConnection conn, string runIdShort, string jobId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + PollDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            var statusJson = await _workspace.InvokeAsync(conn.Endpoint, conn.Token, "/job/status",
                new { job_id = jobId }, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(statusJson);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "exited")
            {
                var resultJson = await _workspace.InvokeAsync(conn.Endpoint, conn.Token, "/job/result",
                    new { job_id = jobId, run_id = runIdShort }, ct).ConfigureAwait(false);
                using var rdoc = JsonDocument.Parse(resultJson);
                var stdout = rdoc.RootElement.TryGetProperty("stdout", out var so) ? so.GetString() ?? "" : "";
                var exit = rdoc.RootElement.TryGetProperty("exit_code", out var ec) ? ec.GetInt32() : 0;
                return (stdout, exit);
            }
        }
        return null;
    }

    /// <summary>Map the `claude -p --output-format json` result envelope onto a RoundOutcome.</summary>
    private RoundOutcome ParseEnvelope(string stdout, int exitCode, string id, string apiKey)
    {
        ClaudeResult? env = null;
        try { env = JsonSerializer.Deserialize<ClaudeResult>(stdout, Json); }
        catch (Exception ex) { _logger.LogWarning(ex, "[ClaudeCode] could not parse result envelope"); }

        if (env is null)
            return Fail(id, "Claude Code returned an unrecognized response.", RunFailureClass.ModelProvider);

        var usage = new TokenUsage
        {
            Input = env.Usage?.InputTokens ?? 0,
            CachedInput = env.Usage?.CacheReadInputTokens ?? 0,
            Output = env.Usage?.OutputTokens ?? 0,
        };
        var sessionId = string.IsNullOrEmpty(env.SessionId) ? id : env.SessionId!;
        var finalText = Scrub(env.Result ?? "", apiKey);

        if (exitCode != 0 || env.IsError || (env.Subtype is not null && env.Subtype != "success"))
        {
            var reason = $"Claude Code did not complete successfully (subtype: {env.Subtype ?? "unknown"}).";
            return new RoundOutcome(RoundKind.Final, finalText, [], null, null, null, null, [], usage, sessionId,
                Error: reason, FailureClass: RunFailureClass.ModelProvider);
        }

        OnText?.Invoke(finalText);
        // CompletionId carries the Claude session id — the activity persists it as task.ClaudeSessionId.
        return new RoundOutcome(RoundKind.Final, finalText, [], null, null, null, null, [], usage, sessionId);
    }

    private static RoundOutcome Fail(string id, string error, RunFailureClass cls) =>
        new(RoundKind.Final, "", [], null, null, null, null, [], new TokenUsage(), id, Error: error, FailureClass: cls);

    /// <summary>Redact the API key value and any sk-ant- token so a secret never lands in an artifact,
    /// RunEvent, or log line.</summary>
    private static string Scrub(string text, string? apiKey)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!string.IsNullOrEmpty(apiKey)) text = text.Replace(apiKey, "***");
        return ApiKeyPattern.Replace(text, "***");
    }

    // ── Result envelope shape (claude -p --output-format json) ──────────────────
    private sealed record ClaudeResult(
        string? Type, string? Subtype, string? Result,
        [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string? SessionId,
        [property: System.Text.Json.Serialization.JsonPropertyName("is_error")] bool IsError,
        ClaudeUsage? Usage);

    private sealed record ClaudeUsage(
        [property: System.Text.Json.Serialization.JsonPropertyName("input_tokens")] int InputTokens,
        [property: System.Text.Json.Serialization.JsonPropertyName("output_tokens")] int OutputTokens,
        [property: System.Text.Json.Serialization.JsonPropertyName("cache_read_input_tokens")] int CacheReadInputTokens);
}
