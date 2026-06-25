using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Usage;

/// <summary>One day's usage totals, for the analytics token/cost-over-time chart.</summary>
public class UsageTimePoint
{
    /// <summary>UTC calendar day, ISO "yyyy-MM-dd".</summary>
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("tokens")] public int Tokens { get; set; }
    [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }

    // Token breakdown for that day (drives the interactive chart's hover tooltip).
    [JsonPropertyName("input")] public int Input { get; set; }
    [JsonPropertyName("cachedInput")] public int CachedInput { get; set; }
    [JsonPropertyName("output")] public int Output { get; set; }
    [JsonPropertyName("reasoning")] public int Reasoning { get; set; }

    /// <summary>Per-model breakdown for that day, keyed by "provider/model".</summary>
    [JsonPropertyName("perModel")] public Dictionary<string, ModelDayBucket> PerModel { get; set; } = new();
}

/// <summary>A single model's contribution to one day's usage.</summary>
public class ModelDayBucket
{
    [JsonPropertyName("tokens")] public int Tokens { get; set; }
    [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }
}
