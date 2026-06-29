using TectikaAgents.Core.Interfaces;

/// <summary>No-op IProjectExplorer for tests that exercise runtimes which ignore exploration.</summary>
internal sealed class NullProjectExplorer : IProjectExplorer
{
    public Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default)
        => Task.FromResult(new BoardOverview("b", "Board", Array.Empty<TaskNode>()));
    public Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string query, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<TaskSummary>)Array.Empty<TaskSummary>());
    public Task<TaskDetail?> GetTaskAsync(string taskId, CancellationToken ct = default)
        => Task.FromResult<TaskDetail?>(null);
    public Task<ArtifactView?> GetArtifactAsync(string taskId, int? version, CancellationToken ct = default)
        => Task.FromResult<ArtifactView?>(null);
    public Task<IReadOnlyList<SharedNote>> GetSharedNotesAsync(string taskId, CancellationToken ct = default)
        => Task.FromResult((IReadOnlyList<SharedNote>)Array.Empty<SharedNote>());
}
