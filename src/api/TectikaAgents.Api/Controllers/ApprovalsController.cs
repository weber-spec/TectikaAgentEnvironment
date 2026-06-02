using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/approvals")]
[Authorize]
public class ApprovalsController : ControllerBase
{
    private readonly CosmosDbService _cosmos;
    private readonly ILogger<ApprovalsController> _logger;

    public ApprovalsController(CosmosDbService cosmos, ILogger<ApprovalsController> logger)
    {
        _cosmos = cosmos;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(CancellationToken ct) =>
        Ok(await _cosmos.GetPendingApprovalsAsync(TenantId, ct));

    [HttpPost("{approvalId}/respond")]
    public async Task<IActionResult> Respond(string approvalId, [FromBody] ApprovalResponse response, CancellationToken ct)
    {
        var approval = await _cosmos.GetApprovalAsync(response.RunId, approvalId, ct);
        if (approval is null) return NotFound();
        if (approval.Status != ApprovalStatus.Pending) return Conflict("Approval already resolved.");

        approval.Status = response.Approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        approval.ApprovedBy = UserId;
        approval.ApprovedAt = DateTimeOffset.UtcNow;
        approval.Notes = response.Notes;

        await _cosmos.UpdateApprovalAsync(approval, ct);

        // TODO Phase 2: raise external event on Durable Functions instance
        // await _durableClient.RaiseEventAsync(approval.RunId, $"approval-gate", approval.Status);
        _logger.LogInformation("Approval {ApprovalId} {Status} by {User}", approvalId, approval.Status, UserId);

        return Ok(approval);
    }
}

public record ApprovalResponse(string RunId, bool Approved, string? Notes);
