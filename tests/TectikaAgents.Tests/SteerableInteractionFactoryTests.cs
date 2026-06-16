using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class SteerableInteractionFactoryTests
{
    [Fact]
    public void Approval_control_maps_to_Approval_type_with_stable_id()
    {
        var control = new PendingControl(PendingControlKind.Approval, "Deploy to prod?");
        var i = SteerableInteractionFactory.Build("run1", "task1", "board1", "tenant1", 2, "auditor@x.com", control);

        Assert.Equal("run1-r2-interaction", i.Id);
        Assert.Equal(InteractionType.Approval, i.Type);
        Assert.Equal(InteractionOrigin.Steerable, i.Origin);
        Assert.Equal(InteractionStatus.Pending, i.Status);
        Assert.Equal("Deploy to prod?", i.ActionDescription);
        Assert.Equal(new List<string> { "auditor@x.com" }, i.RequestedFrom);
        Assert.Equal(2, i.StepIndex);
        Assert.Null(i.QuestionOptions);
    }

    [Fact]
    public void HumanInput_with_options_maps_to_Question_with_options()
    {
        var control = new PendingControl(PendingControlKind.HumanInput, "Which DB?", new[] { "Postgres", "Cosmos" });
        var i = SteerableInteractionFactory.Build("run1", "task1", "board1", "tenant1", 0, null, control);

        Assert.Equal(InteractionType.Question, i.Type);
        Assert.Equal("Which DB?", i.Question);
        Assert.Equal(new List<string> { "Postgres", "Cosmos" }, i.QuestionOptions);
        Assert.Empty(i.RequestedFrom);
    }

    [Fact]
    public void Revision_maps_to_free_text_Question()
    {
        var control = new PendingControl(PendingControlKind.Revision, "Please clarify the scope.");
        var i = SteerableInteractionFactory.Build("run1", "task1", "board1", "tenant1", 1, null, control);

        Assert.Equal(InteractionType.Question, i.Type);
        Assert.Equal("Please clarify the scope.", i.Question);
        Assert.Null(i.QuestionOptions);
    }
}
