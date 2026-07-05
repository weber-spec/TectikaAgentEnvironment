using Microsoft.Azure.Functions.Worker;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

public record CompactRunContextInput(string RunId, string TaskId, string BoardId);

/// <summary><see cref="SummaryBrief"/> is the compacted brief to re-seed the next round with, or "" when
/// compaction fell back to a plain clear.</summary>
public record CompactRunContextResult(string SummaryBrief);

/// <summary>Activity wrapper that runs the (non-deterministic) compaction pipeline outside the orchestrator,
/// so the steerable loop can compact mid-run through a deterministic activity boundary.</summary>
public class CompactRunContextActivity
{
    private readonly RunCompactionService _compaction;

    public CompactRunContextActivity(RunCompactionService compaction) => _compaction = compaction;

    [Function(nameof(CompactRunContextActivity))]
    public async Task<CompactRunContextResult> Run([ActivityTrigger] CompactRunContextInput input, FunctionContext ctx)
        => new(await _compaction.CompactAsync(input.BoardId, input.TaskId, ctx.InvocationId, ctx.CancellationToken));
}
