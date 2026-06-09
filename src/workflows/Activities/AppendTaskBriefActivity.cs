using Microsoft.Azure.Functions.Worker;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class AppendTaskBriefActivity
{
    private readonly WorkflowCosmosService _cosmos;

    public AppendTaskBriefActivity(WorkflowCosmosService cosmos) => _cosmos = cosmos;

    [Function(nameof(AppendTaskBriefActivity))]
    public async Task Run([ActivityTrigger] AppendTaskBriefInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        var task = await _cosmos.GetTaskAsync(input.BoardId, input.TaskId, ct);
        if (task is null) return;
        task.TaskBrief += $"\n{input.AppendText}";
        await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, task.TaskBrief, ct);
    }
}

public record AppendTaskBriefInput(string BoardId, string TaskId, string AppendText);
