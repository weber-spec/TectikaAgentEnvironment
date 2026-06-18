using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Workflows.Services;

/// <summary>Computes cost and records usage: writes one idempotent UsageEvent, then increments the
/// project, board, and task rollups. Skips rollup increments when the event already existed.</summary>
public sealed class UsageRecorder
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly CostCalculator _cost;
    private readonly ILogger<UsageRecorder> _logger;

    public UsageRecorder(WorkflowCosmosService cosmos, CostCalculator cost, ILogger<UsageRecorder> logger)
    {
        _cosmos = cosmos;
        _cost = cost;
        _logger = logger;
    }

    public sealed record Attribution(
        string TenantId, string BoardId, string TaskId, string RunId,
        int Step, int Round, string InvocationId,
        string AgentRoleId, string AgentRoleName,
        string Provider, string Model, string? ModelVersion, string SessionId);

    public async Task RecordAsync(Attribution a, TokenUsage usage, CancellationToken ct)
    {
        if (usage.Total == 0) return;   // nothing billable (e.g. provider omitted usage)

        var at = DateTimeOffset.UtcNow;
        var cost = _cost.Compute(a.Provider, a.Model, usage, at);
        if (cost.PricingMissing)
            _logger.LogWarning("[Usage] no pricing for {Provider}/{Model} — tokens tracked, cost=0", a.Provider, a.Model);

        var ev = new UsageEvent
        {
            Id = UsageEvent.MakeId(a.RunId, a.Step, a.InvocationId, a.Round),
            TenantId = a.TenantId, BoardId = a.BoardId, TaskId = a.TaskId, RunId = a.RunId,
            Step = a.Step, Round = a.Round,
            AgentRoleId = a.AgentRoleId, AgentRoleName = a.AgentRoleName,
            Provider = a.Provider, Model = a.Model, ModelVersion = a.ModelVersion,
            SessionId = a.SessionId, Usage = usage,
            CatalogVersion = cost.CatalogVersion,
            InputPerMillion = cost.InputPerMillion, CachedInputPerMillion = cost.CachedInputPerMillion,
            OutputPerMillion = cost.OutputPerMillion, Currency = cost.Currency,
            CostUsd = cost.CostUsd, PricingMissing = cost.PricingMissing, Timestamp = at,
        };

        var created = await _cosmos.TryCreateUsageEventAsync(ev, ct);
        if (!created)
        {
            _logger.LogDebug("[Usage] event {Id} already exists — skipping rollup increment", ev.Id);
            return;
        }

        await _cosmos.UpdateRollupAsync(a.TenantId, UsageRollup.ProjectId(a.TenantId),
            () => new UsageRollup { Id = UsageRollup.ProjectId(a.TenantId), TenantId = a.TenantId, Scope = UsageScope.Project, ScopeId = a.TenantId },
            r => ApplyShared(r, a.Provider, a.Model, usage, cost.CostUsd), ct);

        await _cosmos.UpdateRollupAsync(a.TenantId, UsageRollup.BoardId(a.BoardId),
            () => new UsageRollup { Id = UsageRollup.BoardId(a.BoardId), TenantId = a.TenantId, Scope = UsageScope.Board, ScopeId = a.BoardId },
            r => ApplyShared(r, a.Provider, a.Model, usage, cost.CostUsd), ct);

        await _cosmos.UpdateRollupAsync(a.TenantId, UsageRollup.TaskId(a.TaskId),
            () => new UsageRollup
            {
                Id = UsageRollup.TaskId(a.TaskId), TenantId = a.TenantId, Scope = UsageScope.Task, ScopeId = a.TaskId,
                CurrentSession = new SessionBucket { SessionId = a.SessionId, Since = at },
            },
            r => ApplyTask(r, a.Provider, a.Model, usage, cost.CostUsd, a.SessionId), ct);
    }

    /// <summary>Increment lifetime + perModel. Used for project/board scopes.</summary>
    public static void ApplyShared(UsageRollup r, string provider, string model, TokenUsage usage, decimal costUsd)
    {
        r.Lifetime.Add(usage, costUsd);
        var key = UsageRollup.ModelKey(provider, model);
        if (!r.PerModel.TryGetValue(key, out var bucket)) { bucket = new UsageBucket(); r.PerModel[key] = bucket; }
        bucket.Add(usage, costUsd);
    }

    /// <summary>Increment lifetime + perModel + currentSession (resetting the session bucket if the
    /// sessionId changed — i.e. a /clear happened since this rollup was last written).</summary>
    public static void ApplyTask(UsageRollup r, string provider, string model, TokenUsage usage, decimal costUsd, string sessionId)
    {
        ApplyShared(r, provider, model, usage, costUsd);
        if (r.CurrentSession is null || r.CurrentSession.SessionId != sessionId)
            r.CurrentSession = new SessionBucket { SessionId = sessionId, Since = DateTimeOffset.UtcNow };
        r.CurrentSession.Add(usage, costUsd);
    }
}
