using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class TokenUsageTests
{
    [Fact]
    public void Total_is_input_plus_output_only()
    {
        var u = new TokenUsage { Input = 1000, CachedInput = 400, Output = 200, Reasoning = 50 };
        Assert.Equal(1200, u.Total);   // total = input + output; cached/reasoning are subsets, not added
    }

    [Fact]
    public void New_fields_default_to_zero()
    {
        var u = new TokenUsage { Input = 10, Output = 5 };
        Assert.Equal(0, u.CachedInput);
        Assert.Equal(0, u.Reasoning);
    }
}
