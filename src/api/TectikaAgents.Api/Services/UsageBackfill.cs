using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Api.Services;

/// <summary>
/// One-time best-effort migration: synthesises UsageRollup records from existing
/// WorkflowRun.TotalTokens so pre-existing runs don't read as zero in the usage UI.
///
/// Enumeration approach: GetBoardsAsync(tenant) → GetTasksByBoardAsync(board) →
/// GetRunsByTaskAsync(task).  Orphaned runs (no board/task path) are not covered.
///
/// Token-split approximation: legacy runs store only TotalTokens with no input/output
/// split, so all tokens are attributed as INPUT to the tenant's default model.
/// This is logged explicitly.
/// </summary>
public sealed class UsageBackfill
{
    private readonly ICosmosDbService _cosmos;
    private readonly CostCalculator _cost;
    private readonly string _defaultModel;
    private readonly string _provider;
    private readonly ILogger<UsageBackfill> _logger;

    public UsageBackfill(
        ICosmosDbService cosmos,
        CostCalculator cost,
        IOptions<FoundrySettings> foundry,
        ILogger<UsageBackfill> logger)
    {
        _cosmos = cosmos;
        _cost = cost;
        _logger = logger;
        _defaultModel = foundry.Value.DefaultModel;
        _provider = foundry.Value.IsOpenAiDirect ? "openai" : "azure";
    }

    /// <summary>
    /// Backfills usage rollups from existing run totals for the given tenant.
    /// Returns the number of runs processed (0 if already backfilled).
    /// Idempotent: if a project rollup already has events, returns 0 immediately.
    /// </summary>
    public async Task<int> RunAsync(string tenantId, CancellationToken ct = default)
    {
        // Idempotency guard — if the project rollup already has data, skip.
        var existing = await _cosmos.GetUsageRollupAsync(tenantId, UsageRollup.ProjectId(tenantId), ct);
        if (existing is { Lifetime.EventCount: > 0 })
        {
            _logger.LogInformation(
                "[UsageBackfill] tenant={TenantId} already has {Events} project-level events — skipping backfill",
                tenantId, existing.Lifetime.EventCount);
            return 0;
        }

        // In-memory rollup accumulators keyed by rollup id.
        var rollups = new Dictionary<string, UsageRollup>();

        UsageRollup GetOrCreate(string id, Func<UsageRollup> factory)
        {
            if (!rollups.TryGetValue(id, out var r)) { r = factory(); rollups[id] = r; }
            return r;
        }

        var boards = (await _cosmos.GetBoardsAsync(tenantId, ct)).ToList();
        var backfilledCount = 0;

        foreach (var board in boards)
        {
            var tasks = (await _cosmos.GetTasksByBoardAsync(board.Id, ct)).ToList();

            foreach (var task in tasks)
            {
                var runs = (await _cosmos.GetRunsByTaskAsync(task.Id, ct))
                    .Where(r => r.TotalTokens > 0)
                    .ToList();

                foreach (var run in runs)
                {
                    // All tokens attributed as INPUT — no input/output split in legacy runs.
                    var usage = new TokenUsage { Input = run.TotalTokens };
                    var at = run.StartedAt == default ? DateTimeOffset.UtcNow : run.StartedAt;
                    var costResult = _cost.Compute(_provider, _defaultModel, usage, at);

                    if (costResult.PricingMissing)
                        _logger.LogWarning(
                            "[UsageBackfill] no pricing for {Provider}/{Model} — run={RunId} tokens tracked, cost=0",
                            _provider, _defaultModel, run.Id);

                    var costUsd = costResult.CostUsd;

                    // Project rollup
                    var proj = GetOrCreate(UsageRollup.ProjectId(tenantId),
                        () => new UsageRollup
                        {
                            Id = UsageRollup.ProjectId(tenantId), TenantId = tenantId,
                            Scope = UsageScope.Project, ScopeId = tenantId,
                        });
                    Accumulate(proj, _provider, _defaultModel, usage, costUsd);

                    // Board rollup
                    var brd = GetOrCreate(UsageRollup.BoardId(board.Id),
                        () => new UsageRollup
                        {
                            Id = UsageRollup.BoardId(board.Id), TenantId = tenantId,
                            Scope = UsageScope.Board, ScopeId = board.Id,
                        });
                    Accumulate(brd, _provider, _defaultModel, usage, costUsd);

                    // Task rollup — seed a CurrentSession so the task table shows data.
                    var sessionId = task.UsageSessionId ?? Guid.NewGuid().ToString();
                    var tsk = GetOrCreate(UsageRollup.TaskId(task.Id),
                        () => new UsageRollup
                        {
                            Id = UsageRollup.TaskId(task.Id), TenantId = tenantId,
                            Scope = UsageScope.Task, ScopeId = task.Id,
                            CurrentSession = new SessionBucket
                            {
                                SessionId = sessionId,
                                Since = run.StartedAt == default ? DateTimeOffset.UtcNow : run.StartedAt,
                            },
                        });
                    AccumulateTask(tsk, _provider, _defaultModel, usage, costUsd, sessionId);

                    backfilledCount++;
                }
            }
        }

        // Persist all accumulated rollups.
        foreach (var rollup in rollups.Values)
        {
            rollup.UpdatedAt = DateTimeOffset.UtcNow;
            await _cosmos.UpsertUsageRollupAsync(rollup, ct);
        }

        _logger.LogInformation(
            "[UsageBackfill] tenant={TenantId} backfilled {Count} run(s) across {Boards} board(s). " +
            "NOTE: all tokens attributed as INPUT to {Provider}/{Model} — no per-run split available in legacy data.",
            tenantId, backfilledCount, boards.Count, _provider, _defaultModel);

        return backfilledCount;
    }

    // ── Accumulation helpers (mirrored from UsageRecorder.ApplyShared / ApplyTask) ──

    private static void Accumulate(UsageRollup r, string provider, string model, TokenUsage usage, decimal costUsd)
    {
        r.Lifetime.Add(usage, costUsd);
        var key = UsageRollup.ModelKey(provider, model);
        if (!r.PerModel.TryGetValue(key, out var bucket)) { bucket = new UsageBucket(); r.PerModel[key] = bucket; }
        bucket.Add(usage, costUsd);
    }

    private static void AccumulateTask(UsageRollup r, string provider, string model, TokenUsage usage, decimal costUsd, string sessionId)
    {
        Accumulate(r, provider, model, usage, costUsd);
        if (r.CurrentSession is null || r.CurrentSession.SessionId != sessionId)
            r.CurrentSession = new SessionBucket { SessionId = sessionId, Since = DateTimeOffset.UtcNow };
        r.CurrentSession.Add(usage, costUsd);
    }
}
