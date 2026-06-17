using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class WorkspaceEnvTests
{
    [Fact]
    public void Standalone_has_only_executor_token_no_repo_vars()
    {
        var env = WorkspaceService.BuildEnv(null, "agent/x", "tok", null);
        Assert.Single(env);
        Assert.Equal("EXECUTOR_TOKEN", env[0].Name);
    }

    [Fact]
    public void With_repo_includes_repo_vars()
    {
        var gh = new GitHubRepoConnection { RepoUrl = "https://github.com/o/r", Owner = "o", Repo = "r", PatSecretName = "s" };
        var env = WorkspaceService.BuildEnv(gh, "agent/x", "tok", "pat");
        Assert.Contains(env, e => e.Name == "REPO_URL");
        Assert.Contains(env, e => e.Name == "GIT_BRANCH");
        Assert.Contains(env, e => e.Name == "GIT_PAT");
        Assert.Contains(env, e => e.Name == "EXECUTOR_TOKEN");
    }
}
