using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class AgentSelfHealTests
{
    private sealed class FakeProvisioner : IAgentProvisioner
    {
        public string? NewHash; public bool Synced = true; public int Calls;
        public Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default)
        {
            Calls++;
            if (NewHash is not null) role.FoundryAgentHash = NewHash;
            return Task.FromResult(new AgentSyncResult(role.FoundryAgentId, Synced));
        }
        public Task DeleteAgentAsync(string? foundryAgentId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static AgentRole Role(string hash) => new() { Id = "r1", TenantId = "t1", FoundryAgentHash = hash };

    [Fact]
    public async Task Returns_true_when_hash_changed_and_synced()
    {
        var p = new FakeProvisioner { NewHash = "new", Synced = true };
        var role = Role("old");
        Assert.True(await AgentSelfHeal.EnsureCurrentAsync(p, role, default));
        Assert.Equal(1, p.Calls);
    }

    [Fact]
    public async Task Returns_false_when_already_current()
    {
        var p = new FakeProvisioner { NewHash = "same", Synced = true };
        var role = Role("same");
        Assert.False(await AgentSelfHeal.EnsureCurrentAsync(p, role, default));
    }

    [Fact]
    public async Task Returns_false_when_sync_failed()
    {
        var p = new FakeProvisioner { NewHash = "new", Synced = false };
        var role = Role("old");
        Assert.False(await AgentSelfHeal.EnsureCurrentAsync(p, role, default));
    }
}
