using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class SteerableInteractionReplyTests
{
    [Fact]
    public void Approved_renders_Approved()
    {
        var i = new HumanInteraction { Type = InteractionType.Approval, Approved = true };
        Assert.Equal("Approved.", SteerableInteractionReply.Render(i));
    }

    [Fact]
    public void Approved_with_notes_appends_notes()
    {
        var i = new HumanInteraction { Type = InteractionType.Approval, Approved = true, Notes = "ship it" };
        Assert.Equal("Approved. ship it", SteerableInteractionReply.Render(i));
    }

    [Fact]
    public void Rejected_renders_Rejected()
    {
        var i = new HumanInteraction { Type = InteractionType.Approval, Approved = false };
        Assert.Equal("Rejected.", SteerableInteractionReply.Render(i));
    }

    [Fact]
    public void Question_renders_the_answer()
    {
        var i = new HumanInteraction { Type = InteractionType.Question, Answer = "Use Postgres" };
        Assert.Equal("Use Postgres", SteerableInteractionReply.Render(i));
    }
}
