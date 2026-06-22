using TectikaAgents.Workflows.Activities;
using Xunit;

namespace TectikaAgents.Tests;

public class WorkspacePromptTests
{
    [Fact] public void Repo_prompt_mentions_git() =>
        Assert.Contains("git", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true, canPushCode: true));

    [Fact] public void Standalone_prompt_says_no_git_repo() =>
        Assert.Contains("no git repo", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: false, canPushCode: true));

    [Fact] public void Both_offer_run_command()
    {
        Assert.Contains("run_command", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true, canPushCode: true));
        Assert.Contains("run_command", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: false, canPushCode: true));
    }

    [Fact] public void No_workspace_prompt_tells_user_to_reassign() =>
        Assert.Contains("workspace permission", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: false, repoConnected: false, canPushCode: false));

    [Fact] public void Push_role_is_told_to_push() =>
        Assert.Contains("git push", RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true, canPushCode: true));

    [Fact] public void No_push_role_is_told_not_to_push()
    {
        var p = RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true, canPushCode: false);
        Assert.Contains("Do NOT push", p);
        Assert.Contains("pushing is disabled", p);
    }
}
