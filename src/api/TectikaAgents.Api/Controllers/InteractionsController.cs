using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/interactions")]
[Authorize]
public class InteractionsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly DurableFunctionsSettings _durableSettings;
    private readonly ILogger<InteractionsController> _logger;

    public InteractionsController(
        ICosmosDbService cosmos,
        IHttpClientFactory httpFactory,
        IOptions<DurableFunctionsSettings> durableSettings,
        ILogger<InteractionsController> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _durableSettings = durableSettings.Value;
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

        // Annotate the agent's control-tool line with the human's answer. The interaction card is the live
        // surface (shown while awaiting); the collapsed tool line is the lasting record — once the card
        // closes the line returns to the transcript and should read "Approved." / the answer, not the
        // stale "awaiting human". Best-effort: never fail the resume over a cosmetic transcript update.
        await PatchControlToolResultAsync(interaction, ct);

        // Wake up the steerable orchestrator: it resumes on a `user_message` string carrying the
        // human's decision, flattened by SteerableInteractionReply.
        var run = await _cosmos.GetRunAsync(interaction.TaskId, req.RunId, ct);
        if (run?.DurableFunctionInstanceId is not null)
            await RaiseUserMessageEventAsync(run.DurableFunctionInstanceId, SteerableInteractionReply.Render(interaction), ct);
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

    // The steerable control tool whose collapsed transcript line should carry this answer. Selection is
    // pipeline-origin (no steerable control tool call), so it has no line to annotate.
    private static string? ControlToolName(InteractionType type) => type switch
    {
        InteractionType.Approval => "request_approval",
        InteractionType.Question => "request_human_input",
        _ => null,
    };

    private async Task PatchControlToolResultAsync(HumanInteraction interaction, CancellationToken ct)
    {
        var toolName = ControlToolName(interaction.Type);
        if (toolName is null) return;
        try
        {
            var events = await _cosmos.GetRunEventsAsync(interaction.TaskId, interaction.StepIndex, ct);
            var step = events.FirstOrDefault(e =>
                e.Kind == RunEventKind.ToolCall
                && e.RunId == interaction.RunId
                && e.Round == interaction.StepIndex
                && e.ToolName == toolName);
            if (step is null) return;
            step.ResultSummary = SteerableInteractionReply.Render(interaction);
            await _cosmos.UpdateRunEventAsync(step, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[InteractionReply] could not annotate control tool result for interaction {InteractionId}", interaction.Id);
        }
    }

    private async Task RaiseUserMessageEventAsync(string instanceId, string text, CancellationToken ct)
    {
        var baseUrl = _durableSettings.StartUrl;
        var managementBase = baseUrl[..baseUrl.IndexOf("/api/", StringComparison.Ordinal)];
        var url = $"{managementBase}/runtime/webhooks/durabletask/instances/{instanceId}/raiseEvent/user_message";
        if (!string.IsNullOrEmpty(_durableSettings.ManagementKey))
            url += $"?code={Uri.EscapeDataString(_durableSettings.ManagementKey)}";

        // The steerable orchestrator awaits WaitForExternalEvent<string>("user_message"), so the event
        // body is the JSON-encoded reply string.
        var body = new StringContent(JsonSerializer.Serialize(text), Encoding.UTF8, "application/json");

        var http = _httpFactory.CreateClient();
        var response = await http.PostAsync(url, body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[InteractionEvent] failed to raise user_message on instance {Instance}: {Status} {Error}", instanceId, response.StatusCode, err);
        }
        else
        {
            _logger.LogInformation("[InteractionEvent] raised user_message on instance {Instance}", instanceId);
        }
    }
}

public record InteractionRespond(
    string RunId,
    int? SelectedIndex,
    string? Answer,
    bool? Approved,
    string? Notes);
