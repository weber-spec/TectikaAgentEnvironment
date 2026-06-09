using System.Collections.Concurrent;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services.MockData;

/// <summary>
/// One-time loader that copies the in-memory demo dataset (<see cref="MockDataSeeder"/>) into the
/// real Cosmos DB through <see cref="ICosmosDbService"/>, so the environment is testable against the
/// real database with the same content the mock served.
///
/// Idempotent: if the <c>default</c> tenant already has any boards, it skips entirely — safe to
/// leave wired on every startup (controlled by the "CosmosDb:SeedData" flag).
/// </summary>
internal static class CosmosDataSeeder
{
    private const string Tenant = "default";

    public static async Task SeedAsync(ICosmosDbService cosmos, ILogger logger, CancellationToken ct = default)
    {
        // ── Idempotency guard — never duplicate or clobber existing data ──────────
        var existing = await cosmos.GetBoardsAsync(Tenant, ct);
        if (existing.Any())
        {
            logger.LogInformation("Cosmos seed: tenant '{Tenant}' already has boards — skipping.", Tenant);
            return;
        }

        // ── Build the dataset by reusing the single source of truth ───────────────
        var boards = new ConcurrentDictionary<string, Board>();
        var tasks = new ConcurrentDictionary<string, AgentTask>();
        var roles = new ConcurrentDictionary<string, AgentRole>();
        var runs = new ConcurrentDictionary<string, WorkflowRun>();
        var artifacts = new ConcurrentDictionary<string, Artifact>();
        var approvals = new ConcurrentDictionary<string, Approval>();
        var edges = new ConcurrentDictionary<string, TaskEdge>();
        MockDataSeeder.Seed(boards, tasks, roles, runs, artifacts, approvals, edges);

        // ── Write through the real service (no Cosmos FK enforcement, but seed in
        //    dependency order for readability: boards → roles → tasks → runs → artifacts → approvals) ──
        foreach (var b in boards.Values) await cosmos.CreateBoardAsync(b, ct);
        foreach (var r in roles.Values) await cosmos.UpsertAgentRoleAsync(r, ct);
        foreach (var t in tasks.Values) await cosmos.CreateTaskAsync(t, ct);
        foreach (var run in runs.Values) await cosmos.CreateRunAsync(run, ct);
        foreach (var a in artifacts.Values) await cosmos.CreateArtifactAsync(a, ct);
        foreach (var ap in approvals.Values) await cosmos.CreateApprovalAsync(ap, ct);
        foreach (var e in edges.Values) await cosmos.CreateEdgeAsync(e, ct);

        logger.LogInformation(
            "Cosmos seed complete — {Boards} boards, {Roles} roles, {Tasks} tasks, {Runs} runs, " +
            "{Artifacts} artifacts, {Approvals} approvals, {Edges} edges written to Cosmos.",
            boards.Count, roles.Count, tasks.Count, runs.Count, artifacts.Count, approvals.Count, edges.Count);
    }
}
