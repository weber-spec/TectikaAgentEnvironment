using TectikaAgents.Core.Interfaces;

namespace TectikaAgents.Workflows.Services;

/// <summary>An explorer that returns nothing — used for one-shot summarization (/compact), where the
/// agent only needs the supplied transcript, not board tools.</summary>
public sealed class NoopProjectExplorer : IProjectExplorer
{
    public Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default)
        => Task.FromResult(new BoardOverview("", "", Array.Empty<TaskNode>()));
    public Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string query, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<TaskSummary>)Array.Empty<TaskSummary>());
    public Task<TaskDetail?> GetTaskAsync(string taskId, CancellationToken ct = default)
        => Task.FromResult<TaskDetail?>(null);
    public Task<ArtifactView?> GetArtifactAsync(string taskId, int? version, CancellationToken ct = default)
        => Task.FromResult<ArtifactView?>(null);
}
