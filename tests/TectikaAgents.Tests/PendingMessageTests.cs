using System.Text.Json;
using TectikaAgents.Core.Models;
using Xunit;

public class PendingMessageTests
{
    [Fact]
    public void PendingMessage_HasDefaults()
    {
        var m = new PendingMessage();
        Assert.False(string.IsNullOrWhiteSpace(m.Id));
        Assert.False(m.Consumed);
        Assert.True(m.CreatedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void PendingMessage_SerializesCamelCase()
    {
        var m = new PendingMessage { Id = "m1", RunId = "r1", TaskId = "t1", Text = "use the cheaper hotel", Consumed = true };
        var json = JsonSerializer.Serialize(m);
        Assert.Contains("\"runId\":\"r1\"", json);
        Assert.Contains("\"text\":\"use the cheaper hotel\"", json);
        Assert.Contains("\"consumed\":true", json);

        var back = JsonSerializer.Deserialize<PendingMessage>(json)!;
        Assert.Equal("t1", back.TaskId);
        Assert.True(back.Consumed);
    }
}
