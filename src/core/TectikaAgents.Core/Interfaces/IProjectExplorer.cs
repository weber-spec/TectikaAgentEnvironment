using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Board-scoped, read-only project exploration for agent tools. An instance is bound to a
/// single board at construction; agents cannot reach other boards.</summary>
public interface IProjectExplorer
{
    Task<BoardOverview> GetBoardOverviewAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaskSummary>> SearchTasksAsync(string query, CancellationToken ct = default);
    Task<TaskDetail?> GetTaskAsync(string taskId, CancellationToken ct = default);
    Task<ArtifactView?> GetArtifactAsync(string taskId, int? version, CancellationToken ct = default);

    /// <summary>Notes the team has explicitly shared with the agent on this task.</summary>
    Task<IReadOnlyList<SharedNote>> GetSharedNotesAsync(string taskId, CancellationToken ct = default);
}

public sealed record BoardOverview(string BoardId, string BoardName, IReadOnlyList<TaskNode> Tasks);
public sealed record TaskNode(string Id, string Title, string Status, string AssigneeId, IReadOnlyList<string> DependsOn);
public sealed record TaskSummary(string Id, string Title, string Status, string? ArtifactSummary);
public sealed record TaskDetail(string Id, string Title, string Description, string Status, string TaskBrief, string? ArtifactSummary);
public sealed record ArtifactView(string TaskId, int Version, string ContentType, string Content);
public sealed record SharedNote(string NoteType, string Body, string Author, DateTimeOffset UpdatedAt);
