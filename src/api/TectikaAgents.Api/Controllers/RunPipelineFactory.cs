using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

/// <summary>Builds a default single-step agent pipeline from a task's assigned agent.</summary>
public static class RunPipelineFactory
{
    public static List<PipelineStep> FromTask(AgentTask task)
    {
        if (task.Assignee.Type != AssigneeType.Agent || string.IsNullOrEmpty(task.Assignee.Id))
            throw new InvalidOperationException($"Task '{task.Id}' has no assigned agent.");
        return new List<PipelineStep>
        {
            new() { Step = 0, Type = StepType.AgentExecution, AgentRoleId = task.Assignee.Id }
        };
    }
}
