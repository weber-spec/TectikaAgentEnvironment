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

    // QA S1 §2.2 — sandbox non-interactivity must be stated so the agent never designs around a TTY.
    [Fact] public void Repo_prompt_states_sandbox_is_non_interactive()
    {
        var p = RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true, canPushCode: true);
        Assert.Contains("NON-INTERACTIVE", p);
        Assert.Contains("stdin", p);
    }

    [Fact] public void Prompt_grants_autonomy_over_asking_to_fix_own_bugs()
    {
        var p = RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true, canPushCode: true);
        Assert.Contains("full autonomy", p);
    }

    // S3 — the deliverable contract: files reach downstream via declare_output's `files` links, not the repo.
    [Fact] public void Prompt_tells_agent_to_deliver_files_via_declare_output_links()
    {
        var p = RunAgentRoundActivity.WorkspacePrompt(canUseWorkspace: true, repoConnected: true, canPushCode: true);
        Assert.Contains("declare_output", p);
        Assert.Contains("`files`", p);
    }
}
