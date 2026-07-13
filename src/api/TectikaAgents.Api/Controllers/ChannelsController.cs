using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/channels")]
[Authorize]
public class ChannelsController : ControllerBase
{
    private readonly IChannelService _channels;
    private readonly SseHub _hub;
    private readonly ILogger<ChannelsController> _logger;

    public ChannelsController(IChannelService channels, SseHub hub, ILogger<ChannelsController> logger)
    {
        _channels = channels;
        _hub = hub;
        _logger = logger;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";
    private string UserId => User.FindFirst("preferred_username")?.Value ?? "unknown";

    /// <summary>Loads a channel only if it exists in the caller's tenant.</summary>
    private async Task<Channel?> AuthorizedChannelAsync(string channelId, CancellationToken ct)
    {
        var channel = await _channels.GetAsync(TenantId, channelId, ct);
        if (channel is null) return null;
        // Named channels are tenant-wide readable; DMs require membership.
        if (channel.Type == ChannelTypes.Dm && channel.Members.All(m => m.Id != UserId)) return null;
        return channel;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _channels.ListForUserAsync(TenantId, UserId, ct));

    [HttpGet("{channelId}")]
    public async Task<IActionResult> Get(string channelId, CancellationToken ct)
    {
        var channel = await AuthorizedChannelAsync(channelId, ct);
        return channel is null ? NotFound() : Ok(channel);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateChannelRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0) return BadRequest("Channel name is required.");
        var channel = await _channels.CreateChannelAsync(TenantId, UserId, name, req.Description ?? string.Empty, req.MemberIds ?? [], ct);
        return Ok(channel);
    }

    [HttpPost("{channelId}/members")]
    public async Task<IActionResult> AddMember(string channelId, [FromBody] AddMemberRequest req, CancellationToken ct)
    {
        if (await AuthorizedChannelAsync(channelId, ct) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.MemberId)) return BadRequest("memberId is required.");
        var channel = await _channels.AddMemberAsync(TenantId, channelId, req.MemberId.Trim(), req.MemberType, ct);
        return channel is null ? NotFound() : Ok(channel);
    }

    [HttpDelete("{channelId}/members/{memberId}")]
    public async Task<IActionResult> RemoveMember(string channelId, string memberId, CancellationToken ct)
    {
        if (await AuthorizedChannelAsync(channelId, ct) is null) return NotFound();
        var channel = await _channels.RemoveMemberAsync(TenantId, channelId, memberId, ct);
        return channel is null ? NotFound() : Ok(channel);
    }

    /// <summary>Get-or-create a DM with another person/agent (deterministic id — never duplicated).</summary>
    [HttpPost("dm")]
    public async Task<IActionResult> OpenDm([FromBody] OpenDmRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.OtherMemberId)) return BadRequest("otherMemberId is required.");
        var dm = await _channels.GetOrCreateDmAsync(TenantId, UserId, req.OtherMemberId.Trim(), req.OtherMemberType, ct);
        return Ok(dm);
    }

    [HttpGet("{channelId}/messages")]
    public async Task<IActionResult> Messages(string channelId, [FromQuery] DateTimeOffset? since, CancellationToken ct)
    {
        if (await AuthorizedChannelAsync(channelId, ct) is null) return NotFound();
        return Ok(await _channels.GetMessagesAsync(TenantId, channelId, since, ct));
    }

    [HttpPost("{channelId}/messages")]
    public async Task<IActionResult> PostMessage(string channelId, [FromBody] PostMessageRequest req, CancellationToken ct)
    {
        if (await AuthorizedChannelAsync(channelId, ct) is null) return NotFound();
        var body = (req.Body ?? string.Empty).Trim();
        if (body.Length == 0) return BadRequest("Message body is required.");
        var message = await _channels.PostMessageAsync(TenantId, channelId, UserId, body, req.Mentions, ct);
        return message is null ? NotFound() : Ok(message);
    }

    [HttpPost("{channelId}/messages/{messageId}/reactions")]
    public async Task<IActionResult> React(string channelId, string messageId, [FromBody] ReactionRequest req, CancellationToken ct)
    {
        if (await AuthorizedChannelAsync(channelId, ct) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Emoji)) return BadRequest("Emoji is required.");
        var message = await _channels.ToggleReactionAsync(TenantId, channelId, messageId, req.Emoji, UserId, ct);
        return message is null ? NotFound() : Ok(message);
    }

    [HttpPost("{channelId}/read")]
    public async Task<IActionResult> MarkRead(string channelId, CancellationToken ct)
    {
        if (await AuthorizedChannelAsync(channelId, ct) is null) return NotFound();
        await _channels.MarkReadAsync(TenantId, channelId, UserId, ct);
        return Ok(new { lastReadAt = DateTimeOffset.UtcNow });
    }

    /// <summary>Per-channel SSE stream — pushes ChannelMessage as JSON events.</summary>
    [HttpGet("{channelId}/stream")]
    [AllowAnonymous]
    public Task Stream(string channelId, CancellationToken ct) =>
        SseEndpoint.RunAsync(Response, _hub, SseKeys.Channel(channelId), ct, _logger);
}

public record CreateChannelRequest(string? Name, string? Description, List<string>? MemberIds);
public record AddMemberRequest(string? MemberId, string? MemberType);
public record OpenDmRequest(string? OtherMemberId, string? OtherMemberType);
public record PostMessageRequest(string? Body, List<string>? Mentions);
// ReactionRequest is shared with CommentsController (see CommentRequests.cs).
