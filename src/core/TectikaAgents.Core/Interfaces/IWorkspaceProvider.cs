namespace TectikaAgents.Core.Interfaces;

/// <summary>A live sandbox connection (executor endpoint + auth token + run-scoped worktree id).</summary>
public sealed record WorkspaceConnection(string Endpoint, string Token, string? RunId = null);

/// <summary>Supplies a sandbox workspace on demand. Implementations provision lazily (first call)
/// and cache, so calling repeatedly within/across a run returns the same workspace. Returns null
/// when no sandbox is available (e.g. provisioning unavailable / the compact path).</summary>
public interface IWorkspaceProvider
{
    Task<WorkspaceConnection?> EnsureAsync(CancellationToken ct = default);

    /// <summary>The real reason the last provisioning attempt failed (e.g. a Key Vault 403 or an ACI
    /// health timeout), or null if it hasn't failed. <see cref="EnsureAsync"/> returns null on failure
    /// (so the model gets a clean tool error); this carries the accurate cause for the run's failure
    /// reason instead of a generic "sandbox could not be started".</summary>
    string? LastError => null;
}
