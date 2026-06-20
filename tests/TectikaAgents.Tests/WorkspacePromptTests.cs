using TectikaAgents.Workflows.Activities;
using Xunit;

namespace TectikaAgents.Tests;

public class WorkspacePromptTests
{
    [Fact] public void Repo_prompt_mentions_git() =>
        Assert.Contains("git", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true));

    [Fact] public void Standalone_prompt_says_no_git_repo() =>
        Assert.Contains("no git repo", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: false));

    [Fact] public void Both_offer_run_command()
    {
        Assert.Contains("run_command", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true));
        Assert.Contains("run_command", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: false));
    }

    [Fact] public void No_workspace_prompt_tells_user_to_reassign() =>
        Assert.Contains("workspace permission", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: false, repoConnected: false));

    [Fact]
    public void ConnectedRepoPrompt_InstructsPushAndPullRequest()
    {
        var prompt = RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true);
        Assert.Contains("push", prompt);
        Assert.Contains("pull request", prompt);
    }
}
