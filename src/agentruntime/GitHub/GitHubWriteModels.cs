using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

/// <summary>Result of a server-side branch merge (GitHub merges API).</summary>
public enum MergeOutcome
{
    /// <summary>A merge commit was created on the base branch.</summary>
    Merged,
    /// <summary>Base already contains head — nothing to merge (GitHub 204).</summary>
    AlreadyUpToDate,
    /// <summary>The merge could not be performed automatically (GitHub 409).</summary>
    Conflict,
}

public sealed record MergeResult(MergeOutcome Outcome, string? Sha = null);

/// <summary>Write access to a board's connected repo. Kept separate from the (cached, read-only)
/// <see cref="IGitHubReadService"/> so writes never sit behind a cache and intent stays explicit.</summary>
public interface IGitHubWriteService
{
    /// <summary>Server-side merge of <paramref name="head"/> into <paramref name="base"/> via the GitHub
    /// merges API. Never force-merges; a non-fast-forwardable divergence returns <see cref="MergeOutcome.Conflict"/>
    /// rather than throwing, so the caller can surface it for manual resolution.</summary>
    Task<MergeResult> MergeAsync(GitHubRepoConnection repo, string @base, string head, string message, CancellationToken ct);
}
