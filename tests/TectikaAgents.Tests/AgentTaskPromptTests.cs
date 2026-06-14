using System.Text.Json;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentTaskPromptTests
{
    [Fact]
    public void Prompt_SerializesAsCamelCase_AndRoundTrips()
    {
        var task = new AgentTask { Id = "t1", Prompt = "Write the API client for THIS task." };

        var json = JsonSerializer.Serialize(task);
        Assert.Contains("\"prompt\":\"Write the API client for THIS task.\"", json);

        var back = JsonSerializer.Deserialize<AgentTask>(json)!;
        Assert.Equal("Write the API client for THIS task.", back.Prompt);
    }

    [Fact]
    public void Prompt_DefaultsToNull()
    {
        Assert.Null(new AgentTask().Prompt);
    }
}
