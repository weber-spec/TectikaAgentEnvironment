using System.Collections.Generic;
using System.Linq;
using TectikaAgents.AgentRuntime.Mcp;
using TectikaAgents.Core.Models;
using Xunit;

public class ConnectionResolverTests
{
    private static Connection Conn(string id, string catalogId, ConnectionStatus status = ConnectionStatus.Connected) =>
        new() { Id = id, TenantId = "t1", CatalogId = catalogId, Status = status };

    private static AgentRole RoleWith(params string[] connectionIds) => new()
    {
        Connections = connectionIds.Select(id => new AgentConnectionRef { ConnectionId = id, CatalogId = "slack" }).ToList()
    };

    private static Board BoardWith(params string[] connectionIds) => new()
    {
        Connections = connectionIds.Select(id => new BoardConnectionBinding { ConnectionId = id }).ToList()
    };

    [Fact]
    public void Effective_requires_both_agent_reference_and_board_binding()
    {
        var tenant = new[] { Conn("a", "slack"), Conn("b", "slack") };
        // Agent references a+b, but board only enabled a → only a is effective.
        var eff = ConnectionResolver.Effective(RoleWith("a", "b"), BoardWith("a"), tenant);
        Assert.Single(eff);
        Assert.Equal("a", eff[0].Id);
    }

    [Fact]
    public void Effective_excludes_non_connected_connections()
    {
        var tenant = new[] { Conn("a", "slack", ConnectionStatus.Error) };
        var eff = ConnectionResolver.Effective(RoleWith("a"), BoardWith("a"), tenant);
        Assert.Empty(eff);
    }

    [Fact]
    public void Effective_empty_when_board_enables_nothing()
    {
        var tenant = new[] { Conn("a", "slack") };
        Assert.Empty(ConnectionResolver.Effective(RoleWith("a"), BoardWith(), tenant));
    }
}
