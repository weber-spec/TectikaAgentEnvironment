using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.Mcp;

/// <summary>Computes the connections an agent may actually use at runtime on a given board: the tenant
/// connections that are BOTH referenced by the agent role AND enabled (bound) on the board, and currently
/// Connected. This is the single gate the runtime resolves before executing an integration tool.</summary>
public static class ConnectionResolver
{
    public static IReadOnlyList<Connection> Effective(
        AgentRole role, Board board, IEnumerable<Connection> tenantConnections)
    {
        var roleIds = new HashSet<string>(role.Connections.Select(c => c.ConnectionId), StringComparer.Ordinal);
        var boardIds = new HashSet<string>(board.Connections.Select(b => b.ConnectionId), StringComparer.Ordinal);
        return tenantConnections
            .Where(c => c.Status == ConnectionStatus.Connected && roleIds.Contains(c.Id) && boardIds.Contains(c.Id))
            .ToList();
    }
}
