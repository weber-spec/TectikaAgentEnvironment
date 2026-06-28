using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

public class ContextManagerTests
{
    [Fact]
    public void Assemble_IncludesTaskAndUpstreamAndBrief_NoSystemPrompt()
    {
        var role = new AgentRole { Id = "r", DisplayName = "Dev", SystemPrompt = "SECRET-SYS-PROMPT" };
        var task = new AgentTask { Id = "t", Title = "Build X", Description = "do X", TaskBrief = "prior note" };
        var board = new Board { Id = "b" };
        var upstream = new List<Artifact> { new() { TaskId = "u", ContentType = ArtifactContentType.Markdown, Content = "UPSTREAM-DATA" } };

        var text = ContextManager.Assemble(role, task, board, upstream);

        Assert.Contains("Build X", text);
        Assert.Contains("UPSTREAM-DATA", text);
        Assert.Contains("prior note", text);
        Assert.DoesNotContain("SECRET-SYS-PROMPT", text); // system prompt lives on the agent, not here
    }

    [Fact]
    public void Assemble_SurfacesUpstreamDeliverableFileLinks()
    {
        var role = new AgentRole { Id = "r", DisplayName = "Dev", SystemPrompt = "S" };
        var task = new AgentTask { Id = "t", Title = "Implement X" };
        var board = new Board { Id = "b" };
        var upstream = new List<Artifact>
        {
            new()
            {
                TaskId = "u", ContentType = ArtifactContentType.Markdown, Content = "the plan",
                Outputs =
                {
                    new Output
                    {
                        Inline = new InlineContent { Content = "the plan" },
                        Links = { new FileLink { Path = "docs/Plan.md", Source = FileLinkSource.Repo } },
                    },
                },
            },
        };

        var text = ContextManager.Assemble(role, task, board, upstream);

        Assert.Contains("docs/Plan.md", text);   // downstream is told where the deliverable file lives
    }

    [Fact]
    public void Context_InstructsAgentToDeclareOutputsAndSummarize()
    {
        var role = new AgentRole { Id = "r", DisplayName = "Dev", SystemPrompt = "SECRET-SYS-PROMPT" };
        var task = new AgentTask { Id = "t", Title = "Build X", Description = "do X" };
        var board = new Board { Id = "b" };
        var upstream = new List<Artifact>();

        var context = ContextManager.Assemble(role, task, board, upstream);

        Assert.Contains("declare_output", context);
        Assert.Contains("handoff summary", context);
    }
}
