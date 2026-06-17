using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;
using Xunit;

namespace TectikaAgents.Tests;

public class CostCalculatorTests
{
    private static CostCalculator Make() => new(new PricingCatalog
    {
        Version = "test-v1",
        Prices =
        {
            new ModelPrice { Provider = "azure-foundry", Model = "gpt-4o",
                InputPerMillion = 2.50m, CachedInputPerMillion = 1.25m, OutputPerMillion = 10.00m,
                Currency = "USD", EffectiveFrom = DateTimeOffset.Parse("2024-01-01T00:00:00Z") },
            new ModelPrice { Provider = "azure-foundry", Model = "gpt-4o",
                InputPerMillion = 3.00m, CachedInputPerMillion = 1.50m, OutputPerMillion = 12.00m,
                Currency = "USD", EffectiveFrom = DateTimeOffset.Parse("2026-01-01T00:00:00Z") },
        }
    });

    [Fact]
    public void Computes_cost_with_separate_input_cached_output_rates()
    {
        var c = Make();
        var usage = new TokenUsage { Input = 1_000_000, CachedInput = 200_000, Output = 500_000 };
        var r = c.Compute("azure-foundry", "gpt-4o", usage, DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        // newest rate effective <= 2026-06: input 3.00, cached 1.50, output 12.00
        // (1_000_000-200_000)/1e6*3.00 + 200_000/1e6*1.50 + 500_000/1e6*12.00 = 2.40 + 0.30 + 6.00
        Assert.False(r.PricingMissing);
        Assert.Equal(8.70m, r.CostUsd);
        Assert.Equal("USD", r.Currency);
    }

    [Fact]
    public void Picks_rate_effective_at_timestamp()
    {
        var c = Make();
        var usage = new TokenUsage { Input = 1_000_000, Output = 0 };
        var early = c.Compute("azure-foundry", "gpt-4o", usage, DateTimeOffset.Parse("2024-06-01T00:00:00Z"));
        Assert.Equal(2.50m, early.CostUsd);   // old rate
    }

    [Fact]
    public void Missing_model_flags_pricingMissing_and_zero_cost()
    {
        var c = Make();
        var r = c.Compute("anthropic", "claude-opus-4-8", new TokenUsage { Input = 100, Output = 100 }, DateTimeOffset.UtcNow);
        Assert.True(r.PricingMissing);
        Assert.Equal(0m, r.CostUsd);
    }
}
