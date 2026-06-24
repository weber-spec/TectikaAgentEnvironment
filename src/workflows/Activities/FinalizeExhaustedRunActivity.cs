using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>
/// Activity — finalizes a steerable run that ended WITHOUT the agent completing (round cap hit, or no
/// human reply before the AwaitUser timeout). Without this, the loop returns Failed but no artifact is
/// written and the task is left stuck InProgress, silently stranding any partial declared deliverables.
/// This preserves those deliverables as a terminal artifact and marks the task Failed.
/// </summary>
public class FinalizeExhaustedRunActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly ILogger<FinalizeExhaustedRunActivity> _logger;

    public FinalizeExhaustedRunActivity(WorkflowCosmosService cosmos, ILogger<FinalizeExhaustedRunActivity> logger)
    {
        _cosmos = cosmos;
        _logger = logger;
    }

    [Function(nameof(FinalizeExhaustedRunActivity))]
    public async Task Run([ActivityTrigger] FinalizeExhaustedInput input, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        _logger.LogWarning("[FinalizeExhausted] run={RunId} task={Task} reason={Reason}",
            input.RunId, input.TaskId, input.Reason);

        var task = await _cosmos.GetTaskAsync(input.BoardId, input.TaskId, ct);
        if (task is null) return;

        var outputs = task.PendingOutputs.Where(o => o.IsValid()).ToList();
        var existing = await _cosmos.GetUpstreamArtifactsAsync([input.TaskId], ct);
        var nextVersion = (existing.MaxBy(a => a.Version)?.Version ?? 0) + 1;

        var summary = $"Run did not complete: {input.Reason}. "
            + (outputs.Count > 0
                ? $"{outputs.Count} partial deliverable(s) preserved below."
                : "No deliverables were declared before the run stopped.");

        var artifact = new Artifact
        {
            TaskId      = input.TaskId,
            RunId       = input.RunId,
            TenantId    = input.TenantId,
            Version     = nextVersion,
            ContentType = ArtifactContentType.Markdown,
            Content     = summary,
            Summary     = summary,
            Outputs     = outputs,
            Origin      = ArtifactOrigin.Agent,
            InternalLogs = [$"Run finalized incomplete: {input.Reason}"],
        };
        var saved = await _cosmos.CreateArtifactAsync(artifact, ct);
        await _cosmos.PatchTaskCurrentArtifactIdAsync(input.BoardId, input.TaskId, saved.Id, ct);  // QA S2 §3.2
        await _cosmos.UpdateTaskStatusAsync(input.BoardId, input.TaskId, AgentTaskStatus.Failed, input.RunId, ct);
    }
}

public record FinalizeExhaustedInput(string RunId, string TaskId, string BoardId, string TenantId, string Reason);
