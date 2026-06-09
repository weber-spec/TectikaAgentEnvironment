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
    public async Task<IActionResult> GetPending(CancellationToken ct) =>
        Ok(await _cosmos.GetPendingInteractionsAsync(TenantId, ct));

    [HttpPost("{interactionId}/respond")]
    public async Task<IActionResult> Respond(string interactionId, [FromBody] InteractionRespond req, CancellationToken ct)
    {
        var interaction = await _cosmos.GetInteractionAsync(req.RunId, interactionId, ct);
        if (interaction is null) return NotFound();
        if (interaction.Status != InteractionStatus.Pending) return Conflict("Interaction already resolved.");

        // Persist response
        interaction.Status      = InteractionStatus.Responded;
        interaction.RespondedBy = UserId;
        interaction.RespondedAt = DateTimeOffset.UtcNow;

        switch (interaction.Type)
        {
            case InteractionType.Selection:
                if (req.SelectedIndex is null || req.SelectedIndex < 0 || req.SelectedIndex >= (interaction.Items?.Count ?? 0))
                    return BadRequest("Invalid selectedIndex.");
                interaction.SelectedIndex = req.SelectedIndex;
                break;

            case InteractionType.Question:
                if (string.IsNullOrWhiteSpace(req.Answer)) return BadRequest("Answer is required.");
                interaction.Answer = req.Answer;
                break;

            case InteractionType.Approval:
                if (req.Approved is null) return BadRequest("Approved field is required.");
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
            _logger.LogWarning("No DurableFunctionInstanceId for run {RunId}", req.RunId);

        // Update task status back to InProgress
        var task = await _cosmos.GetTaskAsync(interaction.BoardId, interaction.TaskId, ct);
        if (task is not null)
        {
            task.Status = AgentTaskStatus.InProgress;
            await _cosmos.UpdateTaskAsync(task, ct);
        }

        _logger.LogInformation("Interaction {Id} ({Type}) responded by {User}", interactionId, interaction.Type, UserId);
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
            _logger.LogError("Failed to raise interaction event: {Status} {Error}", response.StatusCode, err);
        }
        else
        {
            _logger.LogInformation("Raised interaction event '{Event}' on instance {Instance}", eventName, instanceId);
        }
    }
}

public record InteractionRespond(
    string RunId,
    int? SelectedIndex,
    string? Answer,
    bool? Approved,
    string? Notes);
