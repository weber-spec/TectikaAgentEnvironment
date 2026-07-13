using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly NotificationRepository _notificationRepo;
    private readonly SseHub _hub;
    private readonly UserSettingsRepository _userSettingsRepo;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        NotificationRepository notificationRepo,
        SseHub hub,
        UserSettingsRepository userSettingsRepo,
        ILogger<NotificationsController> logger)
    {
        _notificationRepo = notificationRepo;
        _hub = hub;
        _userSettingsRepo = userSettingsRepo;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId => User.FindFirst("preferred_username")?.Value ?? "unknown";

    /// <summary>Returns up to 50 recent notifications for the tenant.</summary>
    [HttpGet]
    public async Task<IActionResult> GetNotifications([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var notifications = await _notificationRepo.GetRecentAsync(TenantId, UserId, limit, ct);
        return Ok(notifications);
    }

    /// <summary>Global SSE stream — pushes NotificationDocument as JSON events.</summary>
    [HttpGet("stream")]
    [AllowAnonymous]
    public Task StreamNotifications(CancellationToken ct) =>
        SseEndpoint.RunAsync(Response, _hub, SseKeys.Notifications, ct, _logger);

    /// <summary>Updates lastReadAt for the current user to now.</summary>
    [HttpPatch("mark-all-read")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var doc = await _userSettingsRepo.GetOrCreateAsync(UserId, ct);
        doc.NotificationsLastReadAt = DateTimeOffset.UtcNow;
        await _userSettingsRepo.UpsertAsync(doc, ct);
        return Ok(new { lastReadAt = doc.NotificationsLastReadAt });
    }
}

/// <summary>User notification preferences endpoints.</summary>
[ApiController]
[Route("api/settings/notifications")]
[Authorize]
public class UserNotificationSettingsController : ControllerBase
{
    private readonly UserSettingsRepository _userSettingsRepo;

    public UserNotificationSettingsController(UserSettingsRepository userSettingsRepo)
    {
        _userSettingsRepo = userSettingsRepo;
    }

    private string UserId => User.FindFirst("preferred_username")?.Value ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var doc = await _userSettingsRepo.GetOrCreateAsync(UserId, ct);
        return Ok(doc);
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] UserSettingsDocument body, CancellationToken ct)
    {
        var doc = await _userSettingsRepo.GetOrCreateAsync(UserId, ct);
        doc.Notifications = body.Notifications;
        // Preserve lastReadAt — client should not overwrite it via PUT
        await _userSettingsRepo.UpsertAsync(doc, ct);
        return Ok(doc);
    }
}
