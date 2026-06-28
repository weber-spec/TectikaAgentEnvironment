namespace TectikaAgents.Core.Models;

/// <summary>Result of folding a run's branch into the LOCAL board-workspace main line (no-repo merge,
/// the local analogue of the GitHub-API merge used for connected repos). Ok = main advanced; otherwise a
/// conflict (main untouched) with the conflicting paths for the user-facing Review message.</summary>
public sealed record WorkspaceMergeResult(bool Ok, IReadOnlyList<string> ConflictFiles)
{
    public static WorkspaceMergeResult Success() => new(true, System.Array.Empty<string>());
    public static WorkspaceMergeResult Conflict(IReadOnlyList<string> files) => new(false, files);
}
