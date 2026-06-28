using TectikaAgents.Workflows.Activities;
using Xunit;

namespace TectikaAgents.Tests;

public class ValidatorPromptTests
{
    // S3 root-cause fix for the doom loop: the validator must validate the upstream DECLARED OUTPUT
    // (record + linked files on the base branch), not the presence of files at a guessed repo path.
    [Fact] public void Validator_points_at_the_declared_output_and_its_files()
    {
        Assert.Contains("declared output", RunAgentRoundActivity.ValidatorPrompt);
        Assert.Contains("read_file", RunAgentRoundActivity.ValidatorPrompt);
    }

    [Fact] public void Validator_still_uses_request_revision()
    {
        Assert.Contains("request_revision", RunAgentRoundActivity.ValidatorPrompt);
    }
}
