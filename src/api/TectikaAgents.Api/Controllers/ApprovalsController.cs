using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/approvals")]
[Authorize]
public class ApprovalsController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ApprovalsController> _logger;

    public ApprovalsController(
        ICosmosDbService cosmos,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<ApprovalsController> logger)
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
        _logger.LogInformation("[ApprovalList] fetching pending approvals for tenant {TenantId}", TenantId);
        var pending = await _cosmos.GetPendingApprovalsAsync(TenantId, ct);
        _logger.LogInformation("[ApprovalList] returning {Count} pending approvals for tenant {TenantId}", pending?.Count() ?? 0, TenantId);
        return Ok(pending);
    }

    [HttpPost("{approvalId}/respond")]
    public async Task<IActionResult> Respond(string approvalId, [FromBody] ApprovalResponse req, CancellationToken ct)
    {
        _logger.LogInformation("[ApprovalDecision] approval {ApprovalId} decision={Decision} run={RunId} by {User}",
            approvalId, req.Approved ? "Approved" : "Rejected", req.RunId, UserId);

        // ── 1. Load + validate approval ───────────────────────────────────────
        var approval = await _cosmos.GetApprovalAsync(req.RunId, approvalId, ct);
        if (approval is null)
        {
            _logger.LogWarning("[ApprovalDecision] approval {ApprovalId} not found for run {RunId}", approvalId, req.RunId);
            return NotFound();
        }
        if (approval.Status != ApprovalStatus.Pending)
        {
            _logger.LogWarning("[ApprovalDecision] approval {ApprovalId} already resolved status={Status}", approvalId, approval.Status);
            return Conflict("Approval already resolved.");
        }

        // ── 2. Persist decision ───────────────────────────────────────────────
        approval.Status     = req.Approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        approval.ApprovedBy = UserId;
        approval.ApprovedAt = DateTimeOffset.UtcNow;
        approval.Notes      = req.Notes;
        await _cosmos.UpdateApprovalAsync(approval, ct);

        // ── 3. Wake up the Durable Functions orchestrator ─────────────────────
        // Load run to get the Durable Functions instance ID
        var run = await _cosmos.GetRunAsync(approval.TaskId, req.RunId, ct);
        if (run?.DurableFunctionInstanceId is not null)
        {
            await RaiseApprovalEventAsync(
                run.DurableFunctionInstanceId,
                approval.StepIndex,
                req.Approved ? "Approved" : "Rejected",
                ct);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("[ApprovalDecision] no DurableFunctionInstanceId for run {RunId} — cannot wake orchestrator", req.RunId);
        }

        _logger.LogInformation("[ApprovalDecision] approval {ApprovalId} resolved status={Status} by {User}", approvalId, approval.Status, UserId);
        return Ok(approval);
    }

    /// <summary>
    /// שולח external event ל-Durable Functions דרך ה-HTTP management API.
    /// POST {host}/runtime/webhooks/durabletask/instances/{instanceId}/raiseEvent/{eventName}
    /// </summary>
    private async Task RaiseApprovalEventAsync(
        string instanceId, int stepIndex, string decision, CancellationToken ct)
    {
        var baseUrl = _config["DurableFunctions:StartUrl"]
            ?? "http://localhost:7071/api/pipelines/start";

        // StartUrl = "http://host/api/pipelines/start" → derive management base
        var managementBase = baseUrl[..baseUrl.IndexOf("/api/", StringComparison.Ordinal)];
        var eventName = $"approval-gate-{stepIndex}";
        var url = $"{managementBase}/runtime/webhooks/durabletask/instances/{instanceId}/raiseEvent/{eventName}";

        var body = new StringContent(
            JsonSerializer.Serialize(decision),
            Encoding.UTF8,
            "application/json");

        var http = _httpFactory.CreateClient();
        var response = await http.PostAsync(url, body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[ApprovalEvent] failed to raise Durable event {Event}: {Status} {Error}", eventName, response.StatusCode, err);
        }
        else
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("[ApprovalEvent] raised Durable event '{Event}' on instance {Instance}", eventName, instanceId);
        }
    }
}

public record ApprovalResponse(string RunId, bool Approved, string? Notes);
