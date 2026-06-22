using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Usage;

/// <summary>One day's usage totals, for the analytics token/cost-over-time chart.</summary>
public class UsageTimePoint
{
    /// <summary>UTC calendar day, ISO "yyyy-MM-dd".</summary>
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("tokens")] public int Tokens { get; set; }
    [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }
}
