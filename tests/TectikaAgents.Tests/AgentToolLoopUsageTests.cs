using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class AgentToolLoopUsageTests
{
    [Fact]
    public async Task Accumulates_input_cached_output_reasoning_across_rounds()
    {
        var explorer = new NullProjectExplorer();
        var loop = new AgentToolLoop(explorer);
        AgentToolLoop.SendRound send = (outputs, ct) =>
        {
            var usage = new TokenUsage { Input = 100, CachedInput = 40, Output = 30, Reasoning = 10 };
            return Task.FromResult(RoundResponse.Final("done", usage));
        };

        var result = await loop.RunAsync(send, maxRounds: 5, onToolCall: (_, _) => { }, CancellationToken.None);

        Assert.Equal(100, result.Usage.Input);
        Assert.Equal(40, result.Usage.CachedInput);
        Assert.Equal(30, result.Usage.Output);
        Assert.Equal(10, result.Usage.Reasoning);
    }
}
