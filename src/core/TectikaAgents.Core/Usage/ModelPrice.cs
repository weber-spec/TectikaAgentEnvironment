using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Usage;

/// <summary>One effective-dated price row for a (provider, model) pair. Rates are per MILLION tokens.</summary>
public class ModelPrice
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("modelVersion")] public string? ModelVersion { get; set; }
    [JsonPropertyName("inputPerMillion")] public decimal InputPerMillion { get; set; }
    [JsonPropertyName("cachedInputPerMillion")] public decimal CachedInputPerMillion { get; set; }
    /// <summary>Cache-WRITE rate (Anthropic prompt caching). 0 for providers that don't bill it separately.</summary>
    [JsonPropertyName("cacheCreationPerMillion")] public decimal CacheCreationPerMillion { get; set; }
    [JsonPropertyName("outputPerMillion")] public decimal OutputPerMillion { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
    [JsonPropertyName("effectiveFrom")] public DateTimeOffset EffectiveFrom { get; set; }
}

public class PricingCatalog
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("prices")] public List<ModelPrice> Prices { get; set; } = [];
}
