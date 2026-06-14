using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

public class ContextManagerBudgetTests
{
    private static AgentRole Role() => new() { Id = "r", DisplayName = "Dev", SystemPrompt = "SECRET-SYS-PROMPT" };
    private static Board Board() => new() { Id = "b", Name = "Proj" };

    [Fact]
    public void Assemble_IncludesPerTaskPrompt()
    {
        var task = new AgentTask { Id = "t", Title = "Build X", Prompt = "TASK-PROMPT-XYZ specifics." };
        var text = ContextManager.Assemble(Role(), task, Board(), Array.Empty<Artifact>());
        Assert.Contains("TASK-PROMPT-XYZ specifics.", text);
    }

    [Fact]
    public void Assemble_UnderBudget_IncludesFullUpstreamContent()
    {
        var task = new AgentTask { Id = "t", Title = "Build X" };
        var upstream = new List<Artifact> {
            new() { TaskId = "u1", ContentType = ArtifactContentType.Markdown,
                    Content = "FULL-UPSTREAM-CONTENT-AAAA", Summary = "UP-SUMMARY" } };

        var text = ContextManager.Assemble(Role(), task, Board(), upstream, maxInputTokens: 100_000);

        Assert.Contains("FULL-UPSTREAM-CONTENT-AAAA", text);
    }

    [Fact]
    public void Assemble_OverBudget_FallsBackToSummaryPlusToolNote()
    {
        var task = new AgentTask { Id = "t", Title = "Build X" };
        var bigContent = new string('Z', 4000); // ~1000 tokens, well over the tiny budget below
        var upstream = new List<Artifact> {
            new() { TaskId = "u1", ContentType = ArtifactContentType.Markdown,
                    Content = bigContent, Summary = "UP-SUMMARY" } };

        var text = ContextManager.Assemble(Role(), task, Board(), upstream, maxInputTokens: 1);

        Assert.DoesNotContain(bigContent, text);
        Assert.Contains("UP-SUMMARY", text);
        Assert.Contains("get_artifact", text);
        Assert.Contains("taskId=u1", text);
    }
}
