using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>
/// A Slack-like conversation. Two kinds share one document, discriminated by <see cref="Type"/>:
/// "channel" (a named group, one is auto-created per board) and "dm" (a 1:1 direct message, exactly
/// two members). Membership is embedded (small cardinality, like Board.Connections) rather than a
/// separate container. Stored in the `channels` container, partitioned by /tenantId.
/// </summary>
public class Channel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;       // partition key

    /// <summary>"channel" | "dm" — string discriminator (avoids enum-casing drift with the frontend,
    /// per the same convention as TaskComment.Kind).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = ChannelTypes.Channel;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Set for the auto-created board channel; null for manually-created channels and DMs.</summary>
    [JsonPropertyName("boardId")]
    public string? BoardId { get; set; }

    /// <summary>True for the auto-provisioned per-board channel (cannot be deleted; membership auto-synced).</summary>
    [JsonPropertyName("isBoardChannel")]
    public bool IsBoardChannel { get; set; }

    [JsonPropertyName("members")]
    public List<ChannelMember> Members { get; set; } = [];

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("archivedAt")]
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>Deterministic id for a DM between two members, so "open DM with X" is idempotent
    /// (get-or-create, never a duplicate). Order-independent by sorting the pair.</summary>
    public static string DmId(string tenantId, string memberA, string memberB)
    {
        var pair = new[] { memberA, memberB };
        Array.Sort(pair, StringComparer.Ordinal);
        return $"dm:{tenantId}:{pair[0]}|{pair[1]}";
    }
}

/// <summary>A person or agent in a channel. For a human, <see cref="Id"/> is the Entra identifier
/// (email/UPN or oid — the same value claims return in preferred_username) so that when a real user
/// directory is added via Entra ID the existing ids resolve to real users without a migration.
/// For an agent, <see cref="Id"/> is the AgentRole id.</summary>
public class ChannelMember
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>"human" | "agent"</summary>
    [JsonPropertyName("memberType")]
    public string MemberType { get; set; } = MemberTypes.Human;

    /// <summary>"owner" | "member"</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = MemberRoles.Member;

    [JsonPropertyName("addedAt")]
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Per-member read marker — drives unread badges without a separate userSettings entry.</summary>
    [JsonPropertyName("lastReadAt")]
    public DateTimeOffset? LastReadAt { get; set; }

    /// <summary>For an agent member: the id of the visible board task that hosts this agent's runs in
    /// this channel. Created lazily on first @mention and reused thereafter (so the agent keeps
    /// conversational memory across mentions via the task's session).</summary>
    [JsonPropertyName("hostTaskId")]
    public string? HostTaskId { get; set; }
}

public static class ChannelTypes
{
    public const string Channel = "channel";
    public const string Dm = "dm";
    public static readonly IReadOnlyList<string> All = [Channel, Dm];
}

public static class MemberTypes
{
    public const string Human = "human";
    public const string Agent = "agent";
    public static readonly IReadOnlyList<string> All = [Human, Agent];
}

public static class MemberRoles
{
    public const string Owner = "owner";
    public const string Member = "member";
}

/// <summary>Keys/values stamped on the hidden host <c>AgentTask.TriggerMeta</c> that backs channel
/// agent chat. Read by the run pipeline (to switch to conversational "channel-chat mode") and by the
/// board views (to hide these tasks). Lives in Core so both the API and the workflows worker share it.</summary>
public static class ChannelTaskMeta
{
    public const string ModeKey = "mode";
    public const string ChannelChatMode = "channelChat";
    public const string ChannelIdKey = "channelId";

    /// <summary>True when a task is a hidden host for channel agent chat (not real board work).</summary>
    public static bool IsChannelChat(AgentTask task) =>
        task.TriggerMeta.TryGetValue(ModeKey, out var m) && m == ChannelChatMode;
}
