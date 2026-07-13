using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Scheduling;

namespace TectikaAgents.Api.Services;

public interface IRunStartService
{
    /// <summary>
    /// Creates a WorkflowRun for the given task, updates the task status to InProgress,
    /// and triggers the Durable Functions pipeline. Returns null if the task is not
    /// eligible (not found, not Backlog, or has no assigned agent).
    /// </summary>
    /// <param name="respectDependencies">
    /// When true, also refuse to start unless every Dependency parent is Done. Run Board sets this:
    /// it fans out from a board snapshot that can be seconds stale, and this is the server's chance to
    /// notice a parent that regressed. A manual per-task run leaves it false and keeps its ability to
    /// force a run over unmet dependencies — that is a feature, not an oversight.
    /// </param>
    Task<WorkflowRun?> StartAsync(string boardId, string taskId, string tenantId,
                                  bool respectDependencies = false, CancellationToken ct = default);
}

public class RunStartService : IRunStartService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly DurableFunctionsSettings _settings;
    private readonly ILogger<RunStartService> _logger;

    public RunStartService(
        ICosmosDbService cosmos,
        IHttpClientFactory httpFactory,
        IOptions<DurableFunctionsSettings> settings,
        ILogger<RunStartService> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<WorkflowRun?> StartAsync(string boardId, string taskId, string tenantId,
                                               bool respectDependencies = false, CancellationToken ct = default)
    {
        _logger.LogInformation("[RunStart] StartAsync board={BoardId} task={TaskId} tenant={TenantId}", boardId, taskId, tenantId);

        // ── 1. Load task ──────────────────────────────────────────────────────
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null)
        {
            _logger.LogWarning("[RunStart] could not start run for task {TaskId} (not found on board {BoardId})", taskId, boardId);
            return null;
        }

        // ── 2. Guard: only start Backlog tasks ────────────────────────────────
        if (task.Status != AgentTaskStatus.Backlog)
        {
            _logger.LogWarning("[RunStart] could not start run for task {TaskId} (not eligible, status {Status})", taskId, task.Status);
            return null;
        }

        // ── 3. Resolve agent role id from assignee ────────────────────────────
        if (task.Assignee.Type != AssigneeType.Agent || string.IsNullOrEmpty(task.Assignee.Id))
        {
            _logger.LogWarning("[RunStart] could not start run for task {TaskId} (no assigned agent)", taskId);
            return null;
        }

        // ── 3.5 Dependency gate (Run Board only) ──────────────────────────────
        // Run Board fans out over a board snapshot that can be up to a poll interval stale, so re-check
        // against Cosmos truth. This must run BEFORE the claim below: a blocked task should never flip to
        // InProgress and then need restoring.
        if (respectDependencies)
        {
            var (satisfied, blocking, missing) = await TaskStartGate.DependenciesSatisfiedAsync(_cosmos, boardId, taskId, ct);
            if (!satisfied)
            {
                if (missing)
                    _logger.LogWarning("[RunStart] task {TaskId} depends on task {UpstreamId}, which no longer exists (dangling edge)", taskId, blocking);
                else
                    _logger.LogInformation("[RunStart] task {TaskId} not started — upstream {UpstreamId} is not Done", taskId, blocking);
                return null;
            }
        }

        // ── 4. Atomically claim the task (Backlog→InProgress) BEFORE persisting the run ──
        // The status check at step 2 is only a fast-path; this compare-and-set is the authoritative
        // guard. Two concurrent StartAsync calls (double-click, fan-in re-trigger, QA retry overlapping
        // a manual run) both pass step 2, but only one wins the claim — the loser returns without
        // creating a run, so the same task can't start two concurrent runs and we leave no orphan run.
        var run = new WorkflowRun
        {
            TenantId           = tenantId,
            TaskId             = taskId,
            BoardId            = boardId,
            Status             = RunStatus.Pending,
            PreviousTaskStatus = AgentTaskStatus.Backlog   // claim only succeeds from Backlog
        };
        var sessionId = task.UsageSessionId ?? Guid.NewGuid().ToString();

        var claimed = await _cosmos.TryClaimTaskForRunAsync(boardId, taskId, run.Id, sessionId, ct);
        if (claimed is null)
        {
            _logger.LogWarning("[RunStart] task {TaskId} was already claimed by another run — not starting a duplicate", taskId);
            return null;
        }

        // ── 5. Persist the WorkflowRun now that we own the task ───────────────
        var savedRun = await _cosmos.CreateRunAsync(run, ct);
        _logger.LogInformation("[RunStart] created run {RunId} for task {TaskId}", savedRun.Id, taskId);

        // ── 6. Trigger steerable orchestrator ────────────────────────────────
        var steerableUrl = BuildSteerableUrl(_settings.StartUrl, _settings.FunctionKey);
        var steerableInput = new
        {
            RunId       = savedRun.Id,
            TaskId      = taskId,
            BoardId     = boardId,
            TenantId    = tenantId,
            AgentRoleId = task.Assignee.Id,
            SeedMessage = (string?)null
        };

        try
        {
            var http    = _httpFactory.CreateClient();
            var content = new StringContent(JsonSerializer.Serialize(steerableInput), Encoding.UTF8, "application/json");
            var res     = await http.PostAsync(steerableUrl, content, ct);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                _logger.LogError("[RunStart] Durable Functions start failed for run {RunId} status={Status} error={Error}", savedRun.Id, res.StatusCode, err);
            }
            else
            {
                var body        = await res.Content.ReadAsStringAsync(ct);
                var durableData = JsonSerializer.Deserialize<JsonElement>(body);
                var instanceId  = durableData.TryGetProperty("instanceId", out var id) ? id.GetString() : null;

                // ── 7. Persist instanceId back to run ─────────────────────────
                if (instanceId is not null)
                {
                    savedRun.DurableFunctionInstanceId = instanceId;
                    await _cosmos.UpdateRunAsync(savedRun, ct);
                    _logger.LogInformation("[RunStart] steerable run started run={RunId} instance={InstanceId}", savedRun.Id, instanceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RunStart] failed to trigger Durable Functions for run {RunId}", savedRun.Id);
            // Don't throw — run is created, trigger can be retried
        }

        return savedRun;
    }

    private static string BuildSteerableUrl(string startUrl, string? functionKey)
    {
        var baseUrl = startUrl.EndsWith("/start", StringComparison.OrdinalIgnoreCase)
            ? startUrl[..^"/start".Length]
            : startUrl;
        var url = $"{baseUrl}/steerable/start";
        if (!string.IsNullOrEmpty(functionKey))
            url += $"?code={Uri.EscapeDataString(functionKey)}";
        return url;
    }
}
