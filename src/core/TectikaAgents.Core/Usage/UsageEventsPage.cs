using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Usage;

public class UsageEventsPage
{
    [JsonPropertyName("items")] public List<UsageEvent> Items { get; set; } = [];
    [JsonPropertyName("continuationToken")] public string? ContinuationToken { get; set; }
}
