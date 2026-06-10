using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Observability;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public class AppendTaskBriefActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly ILogger<AppendTaskBriefActivity> _logger;
    private readonly bool _logSensitive;

    public AppendTaskBriefActivity(WorkflowCosmosService cosmos, IOptions<LoggingSettings> logging, ILogger<AppendTaskBriefActivity> logger)
    {
        _cosmos = cosmos;
        _logger = logger;
        _logSensitive = logging.Value.LogSensitiveContent;
    }

    [Function(nameof(AppendTaskBriefActivity))]
    public async Task Run([ActivityTrigger] AppendTaskBriefInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        _logger.LogInformation("[AppendTaskBrief] appended brief to task {TaskId} text={Text}",
            input.TaskId, SensitiveContent.Format(input.AppendText, _logSensitive));
        var task = await _cosmos.GetTaskAsync(input.BoardId, input.TaskId, ct);
        if (task is null) return;
        task.TaskBrief += $"\n{input.AppendText}";
        await _cosmos.PatchTaskBriefAsync(input.BoardId, input.TaskId, task.TaskBrief, ct);
    }
}

public record AppendTaskBriefInput(string BoardId, string TaskId, string AppendText);
