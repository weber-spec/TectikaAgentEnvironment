using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
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

    [Function(nameof(StartPipeline))]
    public async Task<HttpResponseData> StartPipeline(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "pipelines/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext context)
    {
        _logger.LogInformation("[HttpTrigger] {Function} invoked", nameof(StartPipeline));

        var body = await req.ReadAsStringAsync();
        var input = JsonSerializer.Deserialize<PipelineInput>(body ?? "{}");

        if (input is null)
        {
            var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("Invalid pipeline input");
            return badResponse;
        }

        var instanceId = await durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(TaskPipelineOrchestrator),
            input);

        _logger.LogInformation("[HttpTrigger] started orchestration {InstanceId} for task {TaskId} run {RunId}",
            instanceId, input.TaskId, input.RunId);

        var response = req.CreateResponse(System.Net.HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { instanceId, runId = input.RunId });
        return response;
    }

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

        var instanceId = await durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(SteerableAgentOrchestrator), input);

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

    [Function(nameof(RaiseApprovalEvent))]
    public async Task<HttpResponseData> RaiseApprovalEvent(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "pipelines/{instanceId}/approval/{step}")] HttpRequestData req,
        string instanceId,
        int step,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext context)
    {
        _logger.LogInformation("[HttpTrigger] {Function} invoked instance={InstanceId} step={Step}",
            nameof(RaiseApprovalEvent), instanceId, step);

        var body = await req.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<ApprovalEventPayload>(body ?? "{}");
        var decision = payload?.Approved == true ? "Approved" : "Rejected";

        await durableClient.RaiseEventAsync(instanceId, $"approval-gate-{step}", decision);

        _logger.LogInformation("[HttpTrigger] raised approval event instance={InstanceId} step={Step} decision={Decision}",
            instanceId, step, decision);

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteStringAsync($"Approval event raised: {decision}");
        return response;
    }
}

public record ApprovalEventPayload(bool Approved, string? Notes);
public record UserMessagePayload(string Text);
