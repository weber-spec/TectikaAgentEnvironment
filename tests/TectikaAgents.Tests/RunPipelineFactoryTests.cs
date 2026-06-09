using TectikaAgents.Api.Controllers;
using TectikaAgents.Core.Models;
using Xunit;

public class RunPipelineFactoryTests
{
    [Fact]
    public void BuildsSingleAgentStepFromAssignee()
    {
        var task = new AgentTask { Id = "t", Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = "role-dev" } };
        var pipeline = RunPipelineFactory.FromTask(task);
        Assert.Single(pipeline);
        Assert.Equal("role-dev", pipeline[0].AgentRoleId);
    }

    [Fact]
    public void ThrowsWhenAssigneeIsHuman()
    {
        var task = new AgentTask { Id = "t", Assignee = new TaskAssignee { Type = AssigneeType.Human, Id = "u1" } };
        Assert.Throws<InvalidOperationException>(() => RunPipelineFactory.FromTask(task));
    }
}
