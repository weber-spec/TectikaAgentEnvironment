using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public interface IChannelService
{
    Task<IReadOnlyList<Channel>> ListForUserAsync(string tenantId, string userId, CancellationToken ct = default);
    Task<Channel> CreateChannelAsync(string tenantId, string userId, string name, string description, IEnumerable<string> memberIds, CancellationToken ct = default);
    Task<Channel?> GetAsync(string tenantId, string channelId, CancellationToken ct = default);
    Task<Channel?> AddMemberAsync(string tenantId, string channelId, string memberId, string? memberType, CancellationToken ct = default);
    Task<Channel?> RemoveMemberAsync(string tenantId, string channelId, string memberId, CancellationToken ct = default);
    Task<Channel> GetOrCreateDmAsync(string tenantId, string userId, string otherMemberId, string? otherMemberType, CancellationToken ct = default);
    /// <summary>Messages in the channel (oldest-first). Reconciles any new agent replies from the
    /// host tasks first, so agent responses surface on the caller's poll even without Service Bus.</summary>
    Task<IReadOnlyList<ChannelMessage>> GetMessagesAsync(string tenantId, string channelId, DateTimeOffset? since, CancellationToken ct = default);
    Task<ChannelMessage?> PostMessageAsync(string tenantId, string channelId, string authorId, string body, IEnumerable<string>? mentions, CancellationToken ct = default);
    Task<ChannelMessage?> ToggleReactionAsync(string tenantId, string channelId, string messageId, string emoji, string userId, CancellationToken ct = default);
    Task MarkReadAsync(string tenantId, string channelId, string userId, CancellationToken ct = default);
}

/// <summary>
/// Owns channels/DMs, membership, message posting, server-side mention validation, per-channel SSE
/// broadcast, and the @agent routing decision (drive a visible board task via <see cref="IChatService"/>).
/// Agent replies are mirrored back into the channel lazily on read (works with or without Service Bus).
/// </summary>
public class ChannelService : IChannelService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IChatService _chat;
    private readonly IChannelProvisioningService _provisioning;
    private readonly ChannelConnectionManager _sse;
    private readonly ILogger<ChannelService> _logger;

    public ChannelService(ICosmosDbService cosmos, IChatService chat, IChannelProvisioningService provisioning,
        ChannelConnectionManager sse, ILogger<ChannelService> logger)
    {
        _cosmos = cosmos;
        _chat = chat;
        _provisioning = provisioning;
        _sse = sse;
        _logger = logger;
    }

    private static string InferMemberType(string id, string? explicitType) =>
        explicitType is not null && MemberTypes.All.Contains(explicitType)
            ? explicitType
            : (id.Contains('@') ? MemberTypes.Human : MemberTypes.Agent);

    public async Task<IReadOnlyList<Channel>> ListForUserAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        // Backfill channels for boards/agents that pre-date this feature (once per tenant per process).
        await _provisioning.BackfillTenantAsync(tenantId, ct);
        var all = await _cosmos.GetChannelsByTenantAsync(tenantId, ct);
        // Named channels are visible tenant-wide (Slack-like); DMs only to their participants.
        return all
            .Where(c => c.ArchivedAt is null)
            .Where(c => c.Type == ChannelTypes.Channel || c.Members.Any(m => m.Id == userId))
            .OrderByDescending(c => c.Type == ChannelTypes.Channel)
            .ThenBy(c => c.Name)
            .ToList();
    }

    public async Task<Channel> CreateChannelAsync(string tenantId, string userId, string name, string description, IEnumerable<string> memberIds, CancellationToken ct = default)
    {
        var members = new List<ChannelMember>
        {
            new() { Id = userId, MemberType = MemberTypes.Human, Role = MemberRoles.Owner },
        };
        foreach (var id in memberIds.Distinct().Where(id => id != userId))
            members.Add(new ChannelMember { Id = id, MemberType = InferMemberType(id, null) });

        var channel = new Channel
        {
            TenantId = tenantId,
            Type = ChannelTypes.Channel,
            Name = name.Trim(),
            Description = description.Trim(),
            CreatedBy = userId,
            Members = members,
        };
        return await _cosmos.CreateChannelAsync(channel, ct);
    }

    public Task<Channel?> GetAsync(string tenantId, string channelId, CancellationToken ct = default) =>
        _cosmos.GetChannelAsync(tenantId, channelId, ct);

    public async Task<Channel?> AddMemberAsync(string tenantId, string channelId, string memberId, string? memberType, CancellationToken ct = default)
    {
        var channel = await _cosmos.GetChannelAsync(tenantId, channelId, ct);
        if (channel is null) return null;
        if (channel.Members.All(m => m.Id != memberId))
        {
            channel.Members.Add(new ChannelMember { Id = memberId, MemberType = InferMemberType(memberId, memberType) });
            await _cosmos.UpsertChannelAsync(channel, ct);
            await PostSystemMessageAsync(channel, $"{memberId} joined the channel", ct);
        }
        return channel;
    }

    public async Task<Channel?> RemoveMemberAsync(string tenantId, string channelId, string memberId, CancellationToken ct = default)
    {
        var channel = await _cosmos.GetChannelAsync(tenantId, channelId, ct);
        if (channel is null) return null;
        var removed = channel.Members.RemoveAll(m => m.Id == memberId) > 0;
        if (removed)
        {
            await _cosmos.UpsertChannelAsync(channel, ct);
            await PostSystemMessageAsync(channel, $"{memberId} left the channel", ct);
        }
        return channel;
    }

    public async Task<Channel> GetOrCreateDmAsync(string tenantId, string userId, string otherMemberId, string? otherMemberType, CancellationToken ct = default)
    {
        var id = Channel.DmId(tenantId, userId, otherMemberId);
        var existing = await _cosmos.GetChannelAsync(tenantId, id, ct);
        if (existing is not null) return existing;

        var dm = new Channel
        {
            Id = id,
            TenantId = tenantId,
            Type = ChannelTypes.Dm,
            CreatedBy = userId,
            Members =
            {
                new ChannelMember { Id = userId, MemberType = MemberTypes.Human, Role = MemberRoles.Owner },
                new ChannelMember { Id = otherMemberId, MemberType = InferMemberType(otherMemberId, otherMemberType) },
            },
        };
        return await _cosmos.CreateChannelAsync(dm, ct);
    }

    public async Task<IReadOnlyList<ChannelMessage>> GetMessagesAsync(string tenantId, string channelId, DateTimeOffset? since, CancellationToken ct = default)
    {
        var channel = await _cosmos.GetChannelAsync(tenantId, channelId, ct);
        if (channel is not null)
            await ReconcileAgentRepliesAsync(channel, ct);
        return await _cosmos.GetChannelMessagesAsync(channelId, since, ct);
    }

    public async Task<ChannelMessage?> PostMessageAsync(string tenantId, string channelId, string authorId, string body, IEnumerable<string>? mentions, CancellationToken ct = default)
    {
        var channel = await _cosmos.GetChannelAsync(tenantId, channelId, ct);
        if (channel is null) return null;

        // Validate mentions against actual members (server is authoritative for routing).
        var memberIds = channel.Members.Select(m => m.Id).ToHashSet();
        var validMentions = (mentions ?? []).Distinct().Where(memberIds.Contains).ToList();

        var message = new ChannelMessage
        {
            ChannelId = channelId,
            TenantId = tenantId,
            AuthorId = authorId,
            AuthorType = channel.Members.FirstOrDefault(m => m.Id == authorId)?.MemberType ?? MemberTypes.Human,
            Kind = ChannelMessageKinds.Message,
            Body = body.Trim(),
            Mentions = validMentions,
        };
        var created = await _cosmos.CreateChannelMessageAsync(message, ct);
        await BroadcastAsync(channelId, created, ct);

        // Route to each mentioned agent (visible board task hosts the run).
        foreach (var member in channel.Members.Where(m => m.MemberType == MemberTypes.Agent && validMentions.Contains(m.Id)))
        {
            try { await RouteToAgentAsync(channel, member, authorId, body.Trim(), ct); }
            catch (Exception ex) { _logger.LogError(ex, "[Channel] agent routing failed channel={Channel} agent={Agent}", channelId, member.Id); }
        }

        return created;
    }

    public async Task<ChannelMessage?> ToggleReactionAsync(string tenantId, string channelId, string messageId, string emoji, string userId, CancellationToken ct = default)
    {
        var message = await _cosmos.GetChannelMessageAsync(channelId, messageId, ct);
        if (message is null || message.DeletedAt is not null) return null;

        var users = message.Reactions.TryGetValue(emoji, out var list) ? list : new List<string>();
        if (users.Contains(userId)) users.Remove(userId);
        else users.Add(userId);
        if (users.Count == 0) message.Reactions.Remove(emoji);
        else message.Reactions[emoji] = users;

        var saved = await _cosmos.UpsertChannelMessageAsync(message, ct);
        await BroadcastAsync(channelId, saved, ct);
        return saved;
    }

    public async Task MarkReadAsync(string tenantId, string channelId, string userId, CancellationToken ct = default)
    {
        var channel = await _cosmos.GetChannelAsync(tenantId, channelId, ct);
        if (channel is null) return;
        var member = channel.Members.FirstOrDefault(m => m.Id == userId);
        if (member is null) return;
        member.LastReadAt = DateTimeOffset.UtcNow;
        await _cosmos.UpsertChannelAsync(channel, ct);
    }

    // ── Internals ────────────────────────────────────────────────────────────────

    /// <summary>Ensure a backing board exists to host this channel's agent runs. Board channels use
    /// their own board (full context); DMs / non-board channels lazily create a HIDDEN backing board
    /// (filtered from the Boards list) that exists only to give the run pipeline a board context.</summary>
    private async Task<string> EnsureHostBoardAsync(Channel channel, string ownerId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(channel.BoardId)) return channel.BoardId!;

        var board = await _cosmos.CreateBoardAsync(new Board
        {
            TenantId = channel.TenantId,
            Name = channel.Type == ChannelTypes.Dm ? $"DM · {string.Join(", ", channel.Members.Select(m => m.Id))}" : $"#{channel.Name}",
            Description = "Hosts agent chat for a channel.",
            OwnerId = ownerId,
            Hidden = true,
        }, ct);
        channel.BoardId = board.Id;
        await _cosmos.UpsertChannelAsync(channel, ct);
        return board.Id;
    }

    private async Task RouteToAgentAsync(Channel channel, ChannelMember agent, string authorId, string body, CancellationToken ct)
    {
        var boardId = await EnsureHostBoardAsync(channel, authorId, ct);

        // Reuse the hidden host task for this (channel, agent) if it still exists (conversational memory).
        AgentTask? task = agent.HostTaskId is not null ? await _cosmos.GetTaskAsync(boardId, agent.HostTaskId, ct) : null;
        if (task is null)
        {
            var role = await _cosmos.GetAgentRoleAsync(channel.TenantId, agent.Id, ct);
            var label = channel.Type == ChannelTypes.Dm ? "DM" : $"#{channel.Name}";
            task = await _cosmos.CreateTaskAsync(new AgentTask
            {
                TenantId = channel.TenantId,
                BoardId = boardId,
                Title = $"{role?.DisplayName ?? agent.Id} · {label}",
                Description = $"Conversation with {role?.DisplayName ?? agent.Id} from channel {label}.",
                Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = agent.Id },
                CreatedBy = authorId,
                // mode=channelChat switches the run pipeline to conversational mode (no artifact) and
                // hides this task from all board views.
                TriggerMeta =
                {
                    [ChannelTaskMeta.ModeKey] = ChannelTaskMeta.ChannelChatMode,
                    [ChannelTaskMeta.ChannelIdKey] = channel.Id,
                },
            }, ct);
            agent.HostTaskId = task.Id;
            await _cosmos.UpsertChannelAsync(channel, ct);
        }

        await _chat.SendAsync(boardId, task.Id, channel.TenantId, body, ct);
    }

    /// <summary>On read, surface two things into the channel (idempotent via deterministic message ids):
    /// (1) each agent's CONVERSATIONAL replies from its hidden channel-chat host task (AgentMessage only —
    /// no artifact); (2) for a board channel, the deliverable artifacts produced by the board's REAL tasks,
    /// so the team sees them in the channel and can ask the agent about them.</summary>
    private async Task ReconcileAgentRepliesAsync(Channel channel, CancellationToken ct)
    {
        // (1) Conversational agent replies from the hidden host tasks.
        foreach (var agent in channel.Members.Where(m => m.MemberType == MemberTypes.Agent && m.HostTaskId is not null))
        {
            try
            {
                var events = await _cosmos.GetRunEventsAsync(agent.HostTaskId!, sinceRound: null, ct);
                foreach (var e in events.Where(e => e.Kind == RunEventKind.AgentMessage && e.Timestamp > agent.AddedAt))
                {
                    var id = ChannelMessage.MirroredId(e.Id);
                    if (await _cosmos.GetChannelMessageAsync(channel.Id, id, ct) is not null) continue;
                    var text = !string.IsNullOrWhiteSpace(e.Detail) ? e.Detail! : (e.Title ?? "");
                    var msg = await _cosmos.CreateChannelMessageAsync(new ChannelMessage
                    {
                        Id = id,
                        ChannelId = channel.Id,
                        TenantId = channel.TenantId,
                        AuthorId = agent.Id,
                        AuthorType = MemberTypes.Agent,
                        Kind = ChannelMessageKinds.AgentMessage,
                        Body = text,
                        RunId = e.RunId,
                        TaskId = agent.HostTaskId,
                        SourceRunEventId = e.Id,
                        CreatedAt = e.Timestamp,
                    }, ct);
                    await BroadcastAsync(channel.Id, msg, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Channel] reply reconcile failed channel={Channel} hostTask={Task}", channel.Id, agent.HostTaskId);
            }
        }

        // (2) Deliverables from the board's REAL tasks (not the hidden channel-chat hosts).
        if (channel.IsBoardChannel && !string.IsNullOrEmpty(channel.BoardId))
        {
            try
            {
                var tasks = await _cosmos.GetTasksByBoardAsync(channel.BoardId!, ct);
                foreach (var t in tasks.Where(t => !ChannelTaskMeta.IsChannelChat(t)
                                                   && t.Status is AgentTaskStatus.Done or AgentTaskStatus.Review))
                {
                    var newest = (await _cosmos.GetArtifactVersionsAsync(t.Id, ct)).OrderByDescending(a => a.Version).FirstOrDefault();
                    if (newest is null) continue;
                    var id = ChannelMessage.ArtifactMirrorId(newest.Id);
                    if (await _cosmos.GetChannelMessageAsync(channel.Id, id, ct) is not null) continue;

                    var isAgent = t.Assignee.Type == AssigneeType.Agent && !string.IsNullOrEmpty(t.Assignee.Id);
                    var msg = await _cosmos.CreateChannelMessageAsync(new ChannelMessage
                    {
                        Id = id,
                        ChannelId = channel.Id,
                        TenantId = channel.TenantId,
                        AuthorId = isAgent ? t.Assignee.Id : (string.IsNullOrEmpty(t.CreatedBy) ? "system" : t.CreatedBy),
                        AuthorType = isAgent ? MemberTypes.Agent : MemberTypes.Human,
                        Kind = ChannelMessageKinds.Artifact,
                        Body = string.IsNullOrWhiteSpace(newest.Summary) ? $"Deliverable from “{t.Title}”" : newest.Summary!,
                        TaskId = t.Id,
                        ArtifactId = newest.Id,
                        SourceRunEventId = $"artifact:{newest.Id}",
                    }, ct);
                    await BroadcastAsync(channel.Id, msg, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Channel] artifact surfacing failed channel={Channel}", channel.Id);
            }
        }
    }

    private async Task PostSystemMessageAsync(Channel channel, string text, CancellationToken ct)
    {
        var msg = await _cosmos.CreateChannelMessageAsync(new ChannelMessage
        {
            ChannelId = channel.Id,
            TenantId = channel.TenantId,
            AuthorId = "system",
            AuthorType = MemberTypes.Human,
            Kind = ChannelMessageKinds.System,
            Body = text,
        }, ct);
        await BroadcastAsync(channel.Id, msg, ct);
    }

    private async Task BroadcastAsync(string channelId, ChannelMessage message, CancellationToken ct)
    {
        try { await _sse.BroadcastAsync(channelId, message, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Channel] broadcast failed channel={Channel}", channelId); }
    }
}
