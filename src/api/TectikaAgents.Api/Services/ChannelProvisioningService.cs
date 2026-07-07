using System.Collections.Concurrent;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public interface IChannelProvisioningService
{
    /// <summary>Create the auto board channel for a board if it doesn't exist yet (idempotent).
    /// Seeds membership with the board owner; more members join via <see cref="SyncMemberAsync"/>.</summary>
    Task<Channel?> EnsureBoardChannelAsync(Board board, CancellationToken ct = default);

    /// <summary>Add a person/agent to the board's channel when they get associated with the board
    /// (e.g. assigned to a task). Best-effort and idempotent — never throws to the caller.</summary>
    Task SyncMemberAsync(string tenantId, string boardId, string memberId, string memberType, CancellationToken ct = default);

    /// <summary>One-time-per-process backfill for a tenant: ensure every existing board has a channel
    /// and seed its membership from the board owner + existing task assignees. Idempotent; covers
    /// boards/agents that pre-date this feature. Runs at most once per tenant per process.</summary>
    Task BackfillTenantAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Keeps each board's auto-created channel in existence and its membership in sync with the board's
/// people/agents. Membership is materialized (there is no board-member entity) from the board owner
/// plus task assignees.
/// </summary>
public class ChannelProvisioningService : IChannelProvisioningService
{
    // Tenants already backfilled in this process (the service is scoped, so the guard is static).
    private static readonly ConcurrentDictionary<string, bool> _backfilled = new();

    private readonly ICosmosDbService _cosmos;
    private readonly ILogger<ChannelProvisioningService> _logger;

    public ChannelProvisioningService(ICosmosDbService cosmos, ILogger<ChannelProvisioningService> logger)
    {
        _cosmos = cosmos;
        _logger = logger;
    }

    public async Task BackfillTenantAsync(string tenantId, CancellationToken ct = default)
    {
        if (!_backfilled.TryAdd(tenantId, true)) return;   // already done this process
        try
        {
            var boards = await _cosmos.GetBoardsAsync(tenantId, ct);
            foreach (var board in boards)
            {
                var channel = await EnsureBoardChannelAsync(board, ct);
                if (channel is null)
                {
                    // Creation failed (e.g. the channels container isn't provisioned yet) — don't mark
                    // the tenant done, so this retries on a later list once the container exists.
                    _backfilled.TryRemove(tenantId, out _);
                    _logger.LogWarning("[ChannelProvision] backfill aborted for tenant {Tenant} — board channel create failed (container missing?)", tenantId);
                    return;
                }
                var tasks = await _cosmos.GetTasksByBoardAsync(board.Id, ct);
                foreach (var a in tasks.Select(t => t.Assignee).Where(a => a is not null && !string.IsNullOrEmpty(a.Id)).DistinctBy(a => a.Id))
                {
                    var memberType = a.Type == AssigneeType.Agent ? MemberTypes.Agent : MemberTypes.Human;
                    await SyncMemberAsync(tenantId, board.Id, a.Id, memberType, ct);
                }
            }
            _logger.LogInformation("[ChannelProvision] backfilled channels for tenant {Tenant}", tenantId);
        }
        catch (Exception ex)
        {
            _backfilled.TryRemove(tenantId, out _);   // let a later call retry
            _logger.LogWarning(ex, "[ChannelProvision] backfill failed for tenant {Tenant}", tenantId);
        }
    }

    public async Task<Channel?> EnsureBoardChannelAsync(Board board, CancellationToken ct = default)
    {
        try
        {
            var existing = await _cosmos.GetChannelsForBoardAsync(board.TenantId, board.Id, ct);
            var boardChannel = existing.FirstOrDefault(c => c.IsBoardChannel);
            if (boardChannel is not null) return boardChannel;

            var channel = new Channel
            {
                TenantId = board.TenantId,
                Type = ChannelTypes.Channel,
                Name = board.Name,
                Description = board.Description,
                BoardId = board.Id,
                IsBoardChannel = true,
                CreatedBy = board.OwnerId,
                Members = string.IsNullOrEmpty(board.OwnerId)
                    ? []
                    : [new ChannelMember { Id = board.OwnerId, MemberType = MemberTypes.Human, Role = MemberRoles.Owner }],
            };
            var created = await _cosmos.CreateChannelAsync(channel, ct);
            _logger.LogInformation("[ChannelProvision] created board channel {ChannelId} for board {BoardId}", created.Id, board.Id);
            return created;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ChannelProvision] failed to ensure board channel for {BoardId}", board.Id);
            return null;
        }
    }

    public async Task SyncMemberAsync(string tenantId, string boardId, string memberId, string memberType, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(memberId)) return;
        try
        {
            var channels = await _cosmos.GetChannelsForBoardAsync(tenantId, boardId, ct);
            var boardChannel = channels.FirstOrDefault(c => c.IsBoardChannel);
            if (boardChannel is null)
            {
                // Board channel not created yet (e.g. board pre-dates this feature) — create then add.
                var board = await _cosmos.GetBoardAsync(tenantId, boardId, ct);
                if (board is null) return;
                boardChannel = await EnsureBoardChannelAsync(board, ct);
                if (boardChannel is null) return;
            }

            if (boardChannel.Members.Any(m => m.Id == memberId)) return;
            boardChannel.Members.Add(new ChannelMember { Id = memberId, MemberType = memberType });
            await _cosmos.UpsertChannelAsync(boardChannel, ct);
            _logger.LogInformation("[ChannelProvision] synced member {Member} into channel {ChannelId}", memberId, boardChannel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ChannelProvision] failed to sync member {Member} for board {BoardId}", memberId, boardId);
        }
    }
}
