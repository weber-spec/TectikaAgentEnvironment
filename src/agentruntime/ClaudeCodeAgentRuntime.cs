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

    // Board-tools MCP: the API's public base URL + the shared run-token signing key. When both are set,
    // the agent gets a per-run .mcp.json pointing at <base>/mcp; otherwise the feature is simply off.
    private readonly string? _mcpApiBaseUrl;
    private readonly string? _mcpSigningKey;

    public ClaudeCodeAgentRuntime(IWorkspaceService workspace, ISecretProvider secrets,
        ILogger<ClaudeCodeAgentRuntime> logger, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _workspace = workspace;
        _secrets = secrets;
        _logger = logger;
        _mcpApiBaseUrl = config["Mcp:ApiBaseUrl"];
        _mcpSigningKey = config["Mcp:SigningKey"];
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

        // ApiKey → ANTHROPIC_API_KEY (pay-as-you-go); OAuthToken → CLAUDE_CODE_OAUTH_TOKEN (Pro/Max
        // subscription, from `claude setup-token`). Only the chosen var is injected, so they can't clash.
        var authEnvVar = req.Role.ClaudeAuth == ClaudeAuthMode.OAuthToken ? "CLAUDE_CODE_OAUTH_TOKEN" : "ANTHROPIC_API_KEY";
        string credential;
        try
        {
            if (string.IsNullOrEmpty(req.Role.ApiKeySecretName))
                return Fail(id, "Claude Code role has no Anthropic credential configured.", RunFailureClass.ModelProvider);
            credential = await _secrets.GetSecretAsync(req.Role.ApiKeySecretName, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
                return Fail(id, "Anthropic credential secret was empty.", RunFailureClass.ModelProvider);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCode] failed to read credential secret for role {Role}", req.Role.Id);
            return Fail(id, "Could not read the Anthropic credential from Key Vault.", RunFailureClass.SandboxInfra);
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

            // Expose the Tectika board tools to claude via a per-run MCP server (the API's /mcp endpoint),
            // written as a project .mcp.json in the worktree. --dangerously-skip-permissions auto-approves the
            // mcp__tectika__* tools; --strict-mcp-config makes claude use ONLY this config.
            var mcpFlag = await WriteMcpConfigAsync(req, conn, runIdShort, ct).ConfigureAwait(false);

            // Resume the existing session on continuation/steering rounds; fresh run otherwise. `--resume`
            // is best-effort: if the session is gone (container recycled), Claude errors and we fall back to
            // a fresh prompt on the next round (the activity rebuilds full context when ClaudeSessionId is null).
            var resume = string.IsNullOrEmpty(req.ThreadId) ? "" : $" --resume {req.ThreadId}";
            var errPath = $".claude-job/{jobId}.err";
            // Capture claude's stderr to a file. On success stdout carries the clean JSON envelope; on the
            // usual failure shape (empty stdout — auth rejected, a --resume of a dead session, or a root/
            // sandbox refusal) echo the exit code + stderr to stdout so the real cause flows back in the
            // result instead of an empty, unparseable response. `exit $rc` preserves claude's real exit code.
            var claude =
                $"claude -p \"$(cat {promptPath})\" --output-format json --model {model} --max-turns {MaxTurns}" +
                $" --dangerously-skip-permissions{mcpFlag}{resume}";
            var cmd =
                $"mkdir -p .claude-job; out=$({claude} 2>{errPath}); rc=$?; " +
                $"if [ -n \"$out\" ]; then printf '%s' \"$out\"; " +
                $"else echo \"claude exited $rc with no stdout. stderr:\"; cat {errPath} 2>/dev/null; fi; exit $rc";

            _logger.LogInformation("[ClaudeCode] starting job {JobId} model={Model} resume={Resume}",
                jobId, model, !string.IsNullOrEmpty(req.ThreadId));

            // The credential is delivered per-invocation in the job env (NOT a container env var: the ACI is
            // per-board/shared, the credential is per-agent). The executor injects it into the spawned process only.
            // IS_SANDBOX=1: the executor runs claude as root, and Claude Code refuses --dangerously-skip-permissions
            // under root unless it detects a sandbox via this env var (otherwise it exits with empty stdout).
            await _workspace.InvokeAsync(conn.Endpoint, conn.Token, "/job/start",
                new { cmd, run_id = runIdShort, job_id = jobId, env = new Dictionary<string, string> { [authEnvVar] = credential, ["IS_SANDBOX"] = "1" } },
                ct).ConfigureAwait(false);

            var resultJson = await PollJobAsync(conn, runIdShort, jobId, ct).ConfigureAwait(false);
            if (resultJson is null)
                return Fail(id, "Claude Code run exceeded the time limit.", RunFailureClass.Exhaustion);

            var (stdout, stderr, exitCode) = resultJson.Value;
            return ParseEnvelope(stdout, stderr, exitCode, id, credential);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClaudeCode] run failed role={Role} task={Task} round={Round}", req.Role.Id, req.Task.Id, req.Round);
            return Fail(id, Scrub(ex.Message, null), RunFailureClass.ModelProvider);
        }
    }

    /// <summary>Poll /job/status until the job exits or the deadline passes; then fetch /job/result.
    /// Returns (stdout, stderr, exitCode) or null on deadline. Safe in a Durable activity (real IO + delays).</summary>
    private async Task<(string Stdout, string Stderr, int ExitCode)?> PollJobAsync(WorkspaceConnection conn, string runIdShort, string jobId, CancellationToken ct)
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
                var stderr = rdoc.RootElement.TryGetProperty("stderr", out var se) ? se.GetString() ?? "" : "";
                var exit = rdoc.RootElement.TryGetProperty("exit_code", out var ec) ? ec.GetInt32() : 0;
                return (stdout, stderr, exit);
            }
        }
        return null;
    }

    /// <summary>Map the `claude -p --output-format json` result envelope onto a RoundOutcome.</summary>
    private RoundOutcome ParseEnvelope(string stdout, string stderr, int exitCode, string id, string credential)
    {
        ClaudeResult? env = null;
        try { env = JsonSerializer.Deserialize<ClaudeResult>(stdout, Json); }
        catch (Exception ex) { _logger.LogWarning(ex, "[ClaudeCode] could not parse result envelope"); }

        if (env is null)
        {
            // The CLI wrote no parseable JSON — it usually died before producing output (auth rejected, a
            // --resume of a dead session, or a root/sandbox refusal), writing the real cause to stderr and
            // exiting non-zero. Surface a scrubbed, bounded snippet so the failure reason says WHY.
            var reason = UnparseableReason(stdout, stderr, exitCode, credential);
            _logger.LogWarning("[ClaudeCode] unparseable result envelope — {Reason}", reason);
            return Fail(id, reason, RunFailureClass.ModelProvider);
        }

        var usage = ExtractUsage(env);
        // Claude Code already computed the REAL cost from the actual Anthropic responses — record it as
        // authoritative (0/absent for subscription/OAuth tokens → null → the activity falls back to the catalog).
        var costUsd = env.TotalCostUsd is > 0 ? env.TotalCostUsd : null;
        var sessionId = string.IsNullOrEmpty(env.SessionId) ? id : env.SessionId!;
        var finalText = Scrub(env.Result ?? "", credential);

        if (exitCode != 0 || env.IsError || (env.Subtype is not null && env.Subtype != "success"))
        {
            var reason = $"Claude Code did not complete successfully (subtype: {env.Subtype ?? "unknown"}).";
            return new RoundOutcome(RoundKind.Final, finalText, [], null, null, null, null, [], usage, sessionId,
                Error: reason, FailureClass: RunFailureClass.ModelProvider, CostUsd: costUsd);
        }

        OnText?.Invoke(finalText);
        // CompletionId carries the Claude session id — the activity persists it as task.ClaudeSessionId.
        return new RoundOutcome(RoundKind.Final, finalText, [], null, null, null, null, [], usage, sessionId, CostUsd: costUsd);
    }

    /// <summary>Prefer `modelUsage` (cumulative across the whole run) over the top-level `usage` (often just
    /// the last turn). Anthropic semantics: input_tokens is already the NON-cached input, with cache read and
    /// cache creation reported separately — sum all three into Input so Total is the true prompt size and the
    /// CostCalculator can split cache read/write onto their own rates.</summary>
    private static TokenUsage ExtractUsage(ClaudeResult env)
    {
        if (env.ModelUsage is { Count: > 0 })
        {
            int input = 0, cacheRead = 0, cacheCreate = 0, output = 0;
            foreach (var mu in env.ModelUsage.Values)
            {
                input       += ReadInt(mu, "inputTokens", "input_tokens");
                output      += ReadInt(mu, "outputTokens", "output_tokens");
                cacheRead   += ReadInt(mu, "cacheReadInputTokens", "cache_read_input_tokens");
                cacheCreate += ReadInt(mu, "cacheCreationInputTokens", "cache_creation_input_tokens");
            }
            return new TokenUsage { Input = input + cacheRead + cacheCreate, CachedInput = cacheRead, CacheCreation = cacheCreate, Output = output };
        }
        var u = env.Usage;
        int cr = u?.CacheReadInputTokens ?? 0, cc = u?.CacheCreationInputTokens ?? 0;
        return new TokenUsage { Input = (u?.InputTokens ?? 0) + cr + cc, CachedInput = cr, CacheCreation = cc, Output = u?.OutputTokens ?? 0 };
    }

    private static int ReadInt(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt32();
        return 0;
    }

    /// <summary>Write a per-run .mcp.json into the worktree and return the CLI flag, or "" when the board-tools
    /// MCP endpoint isn't configured. The bearer is a short-lived HMAC run token scoped to this board/task.</summary>
    private async Task<string> WriteMcpConfigAsync(RoundRequest req, WorkspaceConnection conn, string runIdShort, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_mcpApiBaseUrl) || string.IsNullOrEmpty(_mcpSigningKey))
            return "";

        var runCtx = new TectikaAgents.Core.Mcp.RunContext(req.RunId, req.Task.Id, req.Task.BoardId, req.Task.TenantId, req.Role.Id);
        var token = TectikaAgents.Core.Mcp.RunTokenCodec.Mint(runCtx, _mcpSigningKey, DateTimeOffset.UtcNow.AddMinutes(40));
        var mcpJson = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["tectika"] = new
                {
                    type = "http",
                    url = $"{_mcpApiBaseUrl!.TrimEnd('/')}/mcp",
                    headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
                },
            },
        });
        await _workspace.InvokeAsync(conn.Endpoint, conn.Token, "/write",
            new { path = ".mcp.json", content = mcpJson, run_id = runIdShort }, ct).ConfigureAwait(false);
        return " --mcp-config .mcp.json --strict-mcp-config";
    }

    private static RoundOutcome Fail(string id, string error, RunFailureClass cls) =>
        new(RoundKind.Final, "", [], null, null, null, null, [], new TokenUsage(), id, Error: error, FailureClass: cls);

    /// <summary>Dev-facing reason for an unparseable claude result: bounded, credential-scrubbed snippets of
    /// stderr (where the CLI writes the real cause) and stdout, plus the exit code. Lands in the RunFailed
    /// event Detail so a run shows WHY, not just "unrecognized response".</summary>
    private string UnparseableReason(string stdout, string stderr, int exitCode, string credential)
    {
        string Snippet(string s)
        {
            var t = Scrub(s ?? "", credential).Trim();
            if (t.Length == 0) return "(empty)";
            return t.Length > 500 ? t[..500] + "…" : t;
        }
        return $"Claude Code returned an unrecognized response (exit {exitCode}). " +
               $"stderr: {Snippet(stderr)} | stdout: {Snippet(stdout)}";
    }

    /// <summary>Redact the API key value and any sk-ant- token so a secret never lands in an artifact,
    /// RunEvent, or log line.</summary>
    private static string Scrub(string text, string? secret)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!string.IsNullOrEmpty(secret)) text = text.Replace(secret, "***");
        return ApiKeyPattern.Replace(text, "***");
    }

    // ── Result envelope shape (claude -p --output-format json) ──────────────────
    private sealed record ClaudeResult(
        string? Type, string? Subtype, string? Result,
        [property: System.Text.Json.Serialization.JsonPropertyName("session_id")] string? SessionId,
        [property: System.Text.Json.Serialization.JsonPropertyName("is_error")] bool IsError,
        ClaudeUsage? Usage,
        [property: System.Text.Json.Serialization.JsonPropertyName("total_cost_usd")] decimal? TotalCostUsd,
        [property: System.Text.Json.Serialization.JsonPropertyName("modelUsage")] Dictionary<string, JsonElement>? ModelUsage);

    private sealed record ClaudeUsage(
        [property: System.Text.Json.Serialization.JsonPropertyName("input_tokens")] int InputTokens,
        [property: System.Text.Json.Serialization.JsonPropertyName("output_tokens")] int OutputTokens,
        [property: System.Text.Json.Serialization.JsonPropertyName("cache_read_input_tokens")] int CacheReadInputTokens,
        [property: System.Text.Json.Serialization.JsonPropertyName("cache_creation_input_tokens")] int CacheCreationInputTokens);
}
