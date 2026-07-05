using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Triggers;

/// <summary>/compact — summarize the chat into the TaskBrief via one agent turn, then reset the
/// conversation (like /clear). On any failure it falls back to a plain clear so state is never lost.
/// The pipeline lives in <see cref="RunCompactionService"/> so the steerable orchestrator can reuse it
/// mid-run via an activity.</summary>
public class CompactTrigger
{
    private readonly RunCompactionService _compaction;

    public CompactTrigger(RunCompactionService compaction) => _compaction = compaction;

    [Function(nameof(Compact))]
    public async Task<HttpResponseData> Compact(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "pipelines/compact/{boardId}/{taskId}")] HttpRequestData req,
        string boardId, string taskId, FunctionContext context)
    {
        var brief = await _compaction.CompactAsync(boardId, taskId, context.InvocationId, context.CancellationToken);
        return await Json(req, new { summarized = brief.Length > 0 }, System.Net.HttpStatusCode.OK);
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, object body, System.Net.HttpStatusCode code)
    {
        var res = req.CreateResponse(code);
        await res.WriteAsJsonAsync(body);
        return res;
    }
}
