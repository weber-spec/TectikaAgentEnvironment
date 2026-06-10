using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/interactions")]
[Authorize]
public class InteractionsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<InteractionsController> _logger;

    public InteractionsController(
        ICosmosDbService cosmos,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<InteractionsController> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId   => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(CancellationToken ct)
    {
        _logger.LogInformation("[InteractionList] fetching pending interactions for tenant {TenantId}", TenantId);
        var pending = await _cosmos.GetPendingInteractionsAsync(TenantId, ct);
        _logger.LogInformation("[InteractionList] returning {Count} pending interactions for tenant {TenantId}", pending?.Count() ?? 0, TenantId);
        return Ok(pending);
    }

    [HttpPost("{interactionId}/respond")]
    public async Task<IActionResult> Respond(string interactionId, [FromBody] InteractionRespond req, CancellationToken ct)
    {
        _logger.LogInformation("[InteractionReply] received reply for interaction {InteractionId} run={RunId} by {User}",
            interactionId, req.RunId, UserId);

        var interaction = await _cosmos.GetInteractionAsync(req.RunId, interactionId, ct);
        if (interaction is null)
        {
            _logger.LogWarning("[InteractionReply] interaction {InteractionId} not found for run {RunId}", interactionId, req.RunId);
            return NotFound();
        }
        if (interaction.Status != InteractionStatus.Pending)
        {
            _logger.LogWarning("[InteractionReply] interaction {InteractionId} already resolved status={Status}", interactionId, interaction.Status);
            return Conflict("Interaction already resolved.");
        }

        // Persist response
        interaction.Status      = InteractionStatus.Responded;
        interaction.RespondedBy = UserId;
        interaction.RespondedAt = DateTimeOffset.UtcNow;

        switch (interaction.Type)
        {
            case InteractionType.Selection:
                if (req.SelectedIndex is null || req.SelectedIndex < 0 || req.SelectedIndex >= (interaction.Items?.Count ?? 0))
                {
                    _logger.LogWarning("[InteractionReply] interaction {InteractionId} invalid selectedIndex={SelectedIndex}", interactionId, req.SelectedIndex);
                    return BadRequest("Invalid selectedIndex.");
                }
                interaction.SelectedIndex = req.SelectedIndex;
                break;

            case InteractionType.Question:
                if (string.IsNullOrWhiteSpace(req.Answer))
                {
                    _logger.LogWarning("[InteractionReply] interaction {InteractionId} missing required answer", interactionId);
                    return BadRequest("Answer is required.");
                }
                interaction.Answer = req.Answer;
                break;

            case InteractionType.Approval:
                if (req.Approved is null)
                {
                    _logger.LogWarning("[InteractionReply] interaction {InteractionId} missing required approved field", interactionId);
                    return BadRequest("Approved field is required.");
                }
                interaction.Approved = req.Approved;
                interaction.Notes    = req.Notes;
                break;
        }

        await _cosmos.UpdateInteractionAsync(interaction, ct);

        // Build response payload for orchestrator
        var selectedItem = interaction.Type == InteractionType.Selection && interaction.SelectedIndex.HasValue
            ? interaction.Items?[interaction.SelectedIndex.Value]
            : null;

        var payload = new InteractionResponsePayload(
            interactionId,
            interaction.Type.ToString(),
            interaction.SelectedIndex,
            selectedItem?.Title,
            selectedItem?.Price,
            interaction.Answer,
            interaction.Approved,
            interaction.Notes);

        // Wake up orchestrator
        var run = await _cosmos.GetRunAsync(interaction.TaskId, req.RunId, ct);
        if (run?.DurableFunctionInstanceId is not null)
            await RaiseInteractionEventAsync(run.DurableFunctionInstanceId, interaction.StepIndex, payload, ct);
        else
            _logger.LogWarning("[InteractionReply] no DurableFunctionInstanceId for run {RunId} — cannot wake orchestrator", req.RunId);

        // Update task status back to InProgress
        var task = await _cosmos.GetTaskAsync(interaction.BoardId, interaction.TaskId, ct);
        if (task is not null)
        {
            task.Status = AgentTaskStatus.InProgress;
            await _cosmos.UpdateTaskAsync(task, ct);
        }

        _logger.LogInformation("[InteractionReply] interaction {InteractionId} type={Type} responded by {User}", interactionId, interaction.Type, UserId);
        return Ok(interaction);
    }

    private async Task RaiseInteractionEventAsync(
        string instanceId, int stepIndex, InteractionResponsePayload payload, CancellationToken ct)
    {
        var baseUrl = _config["DurableFunctions:StartUrl"]
            ?? "http://localhost:7071/api/pipelines/start";

        var managementBase = baseUrl[..baseUrl.IndexOf("/api/", StringComparison.Ordinal)];
        var eventName = $"interaction-{stepIndex}";
        var url = $"{managementBase}/runtime/webhooks/durabletask/instances/{instanceId}/raiseEvent/{eventName}";

        var body = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var http = _httpFactory.CreateClient();
        var response = await http.PostAsync(url, body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[InteractionEvent] failed to raise interaction event {Event}: {Status} {Error}", eventName, response.StatusCode, err);
        }
        else
        {
            _logger.LogInformation("[InteractionEvent] raised interaction event '{Event}' on instance {Instance}", eventName, instanceId);
        }
    }
}

public record InteractionRespond(
    string RunId,
    int? SelectedIndex,
    string? Answer,
    bool? Approved,
    string? Notes);
