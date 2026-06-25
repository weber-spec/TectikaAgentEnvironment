namespace TectikaAgents.Core.Interfaces;

/// <summary>A live sandbox connection (executor endpoint + auth token + run-scoped worktree id).</summary>
public sealed record WorkspaceConnection(string Endpoint, string Token, string? RunId = null);

/// <summary>Supplies a sandbox workspace on demand. Implementations provision lazily (first call)
/// and cache, so calling repeatedly within/across a run returns the same workspace. Returns null
/// when no sandbox is available (e.g. provisioning unavailable / the compact path).</summary>
public interface IWorkspaceProvider
{
    Task<WorkspaceConnection?> EnsureAsync(CancellationToken ct = default);
}
