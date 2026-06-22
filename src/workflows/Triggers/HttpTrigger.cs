using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Orchestrators;

namespace TectikaAgents.Workflows.Triggers;

/// <summary>
/// HTTP trigger — ה-.NET API קורא לזה להפעלת pipeline חדש.
/// </summary>
public class HttpTrigger
{
    private readonly ILogger<HttpTrigger> _logger;

    public HttpTrigger(ILogger<HttpTrigger> logger) => _logger = logger;

    [Function(nameof(StartSteerablePipeline))]
    public async Task<HttpResponseData> StartSteerablePipeline(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "pipelines/steerable/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext context)
    {
        var body = await req.ReadAsStringAsync();
        var input = JsonSerializer.Deserialize<SteerableRunInput>(body ?? "{}");
        if (input is null)
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid steerable run input");
            return bad;
        }

        // Deterministic instance id keyed on the run id: a retried/duplicate POST for the same run
        // collides on the existing instance instead of spawning a second orchestration for one run.
        var instanceId = await durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(SteerableAgentOrchestrator), input,
            new StartOrchestrationOptions(InstanceId: $"steer-{input.RunId}"), context.CancellationToken);

        _logger.LogInformation("[HttpTrigger] started steerable run {InstanceId} task {TaskId} run {RunId}",
            instanceId, input.TaskId, input.RunId);

        var response = req.CreateResponse(System.Net.HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { instanceId, runId = input.RunId });
        return response;
    }

    [Function(nameof(RaiseUserMessage))]
    public async Task<HttpResponseData> RaiseUserMessage(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "pipelines/{instanceId}/message")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext context)
    {
        var body = await req.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<UserMessagePayload>(body ?? "{}");
        await durableClient.RaiseEventAsync(instanceId, "user_message", payload?.Text ?? "");

        _logger.LogInformation("[HttpTrigger] raised user_message to instance {InstanceId}", instanceId);
        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteStringAsync("user_message raised");
        return response;
    }

    [Function(nameof(Terminate))]
    public async Task<HttpResponseData> Terminate(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "pipelines/{instanceId}/terminate")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext context)
    {
        await durableClient.TerminateInstanceAsync(instanceId, "user /stop");
        _logger.LogInformation("[HttpTrigger] terminated instance {InstanceId}", instanceId);
        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteStringAsync("terminated");
        return response;
    }
}
public record UserMessagePayload(string Text);
