using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Usage;

public sealed class CostResult
{
    public decimal CostUsd { get; init; }
    public bool PricingMissing { get; init; }
    public string CatalogVersion { get; init; } = "";
    public decimal InputPerMillion { get; init; }
    public decimal CachedInputPerMillion { get; init; }
    public decimal OutputPerMillion { get; init; }
    public string Currency { get; init; } = "USD";
}

/// <summary>Pure cost computation over a pricing catalog. No I/O. Cost is frozen by callers onto events.</summary>
public sealed class CostCalculator
{
    private readonly PricingCatalog _catalog;
    public CostCalculator(PricingCatalog catalog) => _catalog = catalog;

    public string CatalogVersion => _catalog.Version;
    public IReadOnlyList<ModelPrice> Prices => _catalog.Prices;

    public ModelPrice? Resolve(string provider, string model, DateTimeOffset at) =>
        _catalog.Prices
            .Where(p => p.Provider == provider && p.Model == model && p.EffectiveFrom <= at)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefault();

    public CostResult Compute(string provider, string model, TokenUsage usage, DateTimeOffset at)
    {
        var price = Resolve(provider, model, at);
        if (price is null)
            return new CostResult { PricingMissing = true, CatalogVersion = _catalog.Version };

        var nonCachedInput = Math.Max(0, usage.Input - usage.CachedInput);
        var cost =
            nonCachedInput / 1_000_000m * price.InputPerMillion +
            usage.CachedInput / 1_000_000m * price.CachedInputPerMillion +
            usage.Output / 1_000_000m * price.OutputPerMillion;

        return new CostResult
        {
            CostUsd = decimal.Round(cost, 6),
            PricingMissing = false,
            CatalogVersion = _catalog.Version,
            InputPerMillion = price.InputPerMillion,
            CachedInputPerMillion = price.CachedInputPerMillion,
            OutputPerMillion = price.OutputPerMillion,
            Currency = price.Currency,
        };
    }
}
