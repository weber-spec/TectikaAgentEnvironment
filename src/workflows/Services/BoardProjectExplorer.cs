using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>IProjectExplorer bound to one board, backed by WorkflowCosmosService. Thin mapping only —
/// covered by the live smoke test, not an isolated unit test.</summary>
public sealed class BoardProjectExplorer : IProjectExplorer
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly string _boardId;
    private readonly string _tenantId;

    public BoardProjectExplorer(WorkflowCosmosService cosmos, string boardId, string tenantId)
    {
        _cosmos = cosmos; _boardId = boardId; _tenantId = tenantId;
    }

    public async Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default)
    {
        var board = await _cosmos.GetBoardAsync(_boardId, _tenantId, ct);
        var tasks = await _cosmos.GetBoardTasksAsync(_boardId, ct);
        var nodes = new List<TaskNode>();
        foreach (var t in tasks)
        {
            var deps = await _cosmos.GetUpstreamTaskIdsAsync(_boardId, t.Id, ct);
            nodes.Add(new TaskNode(t.Id, t.Title, t.Status.ToString(), t.Assignee.Id, deps));
        }
        return new BoardOverview(_boardId, board?.Name ?? _boardId, nodes);
    }

    public async Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string query, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        var tasks = await _cosmos.GetBoardTasksAsync(_boardId, ct);
        return tasks
            .Where(t => q.Length == 0
                || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (t.Description ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (t.TaskBrief ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(t => new TaskSummary(t.Id, t.Title, t.Status.ToString(), t.ArtifactSummary))
            .ToList();
    }

    public async Task<TaskDetail?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        var t = await _cosmos.GetTaskAsync(_boardId, taskId, ct);
        return t is null ? null
            : new TaskDetail(t.Id, t.Title, t.Description, t.Status.ToString(), t.TaskBrief, t.ArtifactSummary);
    }

    public async Task<ArtifactView?> GetArtifactAsync(string taskId, int? version, CancellationToken ct = default)
    {
        var arts = await _cosmos.GetUpstreamArtifactsAsync([taskId], ct); // latest per task
        var art = version is null ? arts.FirstOrDefault() : arts.FirstOrDefault(a => a.Version == version);
        return art is null ? null
            : new ArtifactView(art.TaskId, art.Version, art.ContentType.ToString(), art.Content);
    }
}
