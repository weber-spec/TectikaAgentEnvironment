using System.Text.Json;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentTaskChatClearedAtTests
{
    [Fact]
    public void ChatClearedAt_DefaultsNull_AndRoundTrips()
    {
        Assert.Null(new AgentTask().ChatClearedAt);
        var t = new AgentTask { Id = "t", ChatClearedAt = DateTimeOffset.Parse("2026-06-15T10:00:00Z") };
        var json = JsonSerializer.Serialize(t);
        Assert.Contains("\"chatClearedAt\":", json);
        Assert.Equal(t.ChatClearedAt, JsonSerializer.Deserialize<AgentTask>(json)!.ChatClearedAt);
    }
}
