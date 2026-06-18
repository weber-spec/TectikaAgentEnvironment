using TectikaAgents.Core.Usage;
using Xunit;

namespace TectikaAgents.Tests;

public class PricingCatalogTests
{
    [Fact]
    public void Embedded_catalog_loads_and_has_a_version_and_gpt4o()
    {
        var catalog = PricingCatalogLoader.LoadEmbedded();
        Assert.False(string.IsNullOrWhiteSpace(catalog.Version));
        Assert.Contains(catalog.Prices, p => p.Provider == "azure-foundry" && p.Model == "gpt-4o");
    }

    [Fact]
    public void Gpt4o_output_rate_exceeds_input_rate()
    {
        var catalog = PricingCatalogLoader.LoadEmbedded();
        var p = catalog.Prices.Single(x => x.Provider == "azure-foundry" && x.Model == "gpt-4o");
        Assert.True(p.OutputPerMillion > p.InputPerMillion);
        Assert.True(p.CachedInputPerMillion < p.InputPerMillion);
    }
}
