using TectikaAgents.Core.Usage;
using Xunit;

namespace TectikaAgents.Tests;

public class UsageEventTests
{
    [Fact]
    public void DeterministicId_is_run_step_invocation_round()
    {
        var id = UsageEvent.MakeId("run-1", 2, "inv-abc", 3);
        Assert.Equal("run-1:2:inv-abc:3", id);
    }
}
