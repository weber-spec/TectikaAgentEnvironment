using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>Builds the Code (external/github) Output for a task from its compare result + optional PR.
/// Pure — no I/O — so the locator contract is unit-tested.</summary>
public static class CodeOutputBuilder
{
    public static Output Build(GitHubRepoConnection repo, string baseBranch, string headBranch, CompareResult cmp, PullRequestInfo? pr)
    {
        var locator = new Dictionary<string, string>
        {
            ["owner"] = repo.Owner,
            ["repo"] = repo.Repo,
            ["branch"] = headBranch,
            ["base"] = baseBranch,
            ["headSha"] = cmp.HeadSha,
            ["filesChanged"] = cmp.FilesChanged.ToString(),
            ["additions"] = cmp.Additions.ToString(),
            ["deletions"] = cmp.Deletions.ToString(),
        };
        if (pr is not null)
        {
            locator["prNumber"] = pr.Number.ToString();
            locator["prUrl"] = pr.Url;
        }
        return new Output
        {
            Kind = OutputKind.Code,
            Label = pr is not null ? $"PR #{pr.Number}" : headBranch,
            External = new ExternalRef
            {
                Provider = "github",
                Locator = locator,
                PreviewUrl = pr?.Url,
            },
        };
    }
}
