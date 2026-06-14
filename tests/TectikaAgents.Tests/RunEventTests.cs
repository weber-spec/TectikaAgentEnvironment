using System.Text.Json;
using TectikaAgents.Core.Models;
using Xunit;

public class RunEventTests
{
    [Fact]
    public void RunEvent_HasDefaults()
    {
        var e = new RunEvent();
        Assert.False(string.IsNullOrWhiteSpace(e.Id));          // GUID by default
        Assert.Equal(RunEventKind.Thinking, e.Kind);            // first enum member
        Assert.True(e.Timestamp <= DateTimeOffset.UtcNow);
        Assert.Null(e.ParentId);                                // round-level by default
    }

    [Fact]
    public void RunEvent_SerializesCamelCase()
    {
        var e = new RunEvent
        {
            Id = "ev1", TaskId = "t1", RunId = "r1", Round = 2,
            ParentId = "ev0", Kind = RunEventKind.ToolCall,
            Title = "Gathering data about the project",
            ToolName = "GetArtifact", ToolArgsSummary = "taskId=u1",
            ResultSummary = "1.2 KB markdown"
        };

        var json = JsonSerializer.Serialize(e);

        Assert.Contains("\"taskId\":\"t1\"", json);
        Assert.Contains("\"parentId\":\"ev0\"", json);
        Assert.Contains("\"round\":2", json);
        Assert.Contains("\"toolName\":\"GetArtifact\"", json);
        Assert.Contains("\"kind\":\"ToolCall\"", json);          // enum serialized as string name

        var back = JsonSerializer.Deserialize<RunEvent>(json)!;
        Assert.Equal(RunEventKind.ToolCall, back.Kind);
        Assert.Equal("Gathering data about the project", back.Title);
    }
}
