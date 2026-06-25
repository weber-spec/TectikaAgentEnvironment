using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Api.Controllers;

/// <summary>QA S3 §4.5 — one consolidated board-state read so the board does a SINGLE poll per tick
/// (tasks + edges + per-task usage + active runs) instead of fanning out tasks + edges + N usage + N run
/// requests, which kept the page from ever reaching network-idle. Live status still flows instantly over
/// SSE; this snapshot is the resilient reconcile fallback.</summary>
[ApiController]
[Route("api/boards/{boardId}/state")]
[Authorize]
public class BoardStateController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;

    public BoardStateController(ICosmosDbService cosmos) => _cosmos = cosmos;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    [HttpGet]
    public async Task<IActionResult> Get(string boardId, CancellationToken ct)
    {
        var tasks = (await _cosmos.GetTasksByBoardAsync(boardId, ct)).ToList();
        var edges = (await _cosmos.GetEdgesByBoardAsync(boardId, ct)).ToList();
        var tenantId = TenantId;

        // For each task that has a run, fetch its usage rollup + the run itself. Fanned out across tasks
        // (concurrent), so the whole snapshot is one round-trip for the client.
        var withRun = tasks.Where(t => !string.IsNullOrEmpty(t.WorkflowRunId)).ToList();
        var perTask = await Task.WhenAll(withRun.Select(async t => (
            TaskId: t.Id,
            Usage: await _cosmos.GetUsageRollupAsync(tenantId, UsageRollup.TaskId(t.Id), ct),
            Run:   await _cosmos.GetRunAsync(t.Id, t.WorkflowRunId!, ct))));

        var usageByTaskId = perTask.Where(x => x.Usage is not null).ToDictionary(x => x.TaskId, x => x.Usage!);
        var runsById      = perTask.Where(x => x.Run is not null).ToDictionary(x => x.Run!.Id, x => x.Run!);

        return Ok(new BoardStateResponse(tasks, edges, usageByTaskId, runsById));
    }

    public record BoardStateResponse(
        [property: JsonPropertyName("tasks")] IReadOnlyList<AgentTask> Tasks,
        [property: JsonPropertyName("edges")] IReadOnlyList<TaskEdge> Edges,
        [property: JsonPropertyName("usageByTaskId")] IReadOnlyDictionary<string, UsageRollup> UsageByTaskId,
        [property: JsonPropertyName("runsById")] IReadOnlyDictionary<string, WorkflowRun> RunsById);
}
