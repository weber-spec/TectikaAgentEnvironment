using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class WorkspaceEnvTests
{
    [Fact]
    public void Standalone_has_only_executor_token_no_repo_vars()
    {
        var env = WorkspaceService.BuildEnv(null, "tok", null);
        Assert.Single(env);
        Assert.Equal("EXECUTOR_TOKEN", env[0].Name);
    }

    [Fact]
    public void With_repo_includes_repo_vars()
    {
        var gh = new GitHubRepoConnection { RepoUrl = "https://github.com/o/r", Owner = "o", Repo = "r", PatSecretName = "s" };
        var env = WorkspaceService.BuildEnv(gh, "tok", "pat");
        Assert.Contains(env, e => e.Name == "REPO_URL");
        Assert.Contains(env, e => e.Name == "GIT_PAT");
        Assert.Contains(env, e => e.Name == "EXECUTOR_TOKEN");
    }

    [Fact]
    public void Push_permission_flows_to_GIT_CAN_PUSH()
    {
        var gh = new GitHubRepoConnection { RepoUrl = "https://github.com/o/r", Owner = "o", Repo = "r", PatSecretName = "s" };

        var noPush = WorkspaceService.BuildEnv(gh, "tok", "pat", canPush: false);
        Assert.Equal("false", noPush.Single(e => e.Name == "GIT_CAN_PUSH").Value);

        var canPush = WorkspaceService.BuildEnv(gh, "tok", "pat", canPush: true);
        Assert.Equal("true", canPush.Single(e => e.Name == "GIT_CAN_PUSH").Value);
    }

    // ── ContainerNameFor: provisioning and teardown MUST derive the same ACI name from a runId.
    //    A mismatch (or a name ACI rejects) is exactly what leaves crash-looping groups orphaned.

    [Fact]
    public void ContainerName_is_tws_prefix_plus_first8_of_runId()
    {
        Assert.Equal("tws-54a86b65", WorkspaceService.ContainerNameFor("54a86b65-7502-4d9d-b3d2-5830c7fbadfe"));
    }

    [Fact]
    public void ContainerName_is_lowercased_for_aci_dns_label()
    {
        // ACI DNS labels must be lowercase; an upper-case runId must still yield a valid name.
        Assert.Equal("tws-abcd1234", WorkspaceService.ContainerNameFor("ABCD1234-FFFF"));
    }

    [Fact]
    public void ContainerName_handles_runId_shorter_than_8_chars()
    {
        Assert.Equal("tws-abc", WorkspaceService.ContainerNameFor("abc"));
    }

    [Fact]
    public void ContainerName_is_deterministic_for_same_runId()
    {
        const string runId = "deadBEEF-1111-2222";
        Assert.Equal(WorkspaceService.ContainerNameFor(runId), WorkspaceService.ContainerNameFor(runId));
    }
}
