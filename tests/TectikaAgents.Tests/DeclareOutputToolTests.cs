using System.Text.Json;
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class DeclareOutputToolTests
{
    private static ToolCall FC(string name, object args) =>
        new(name, JsonSerializer.Serialize(args), $"call_{name}");

    private static Task<RoundProcessResult> Run(params ToolCall[] calls) =>
        RoundExecutor.ExecuteOneRoundAsync(
            RoundResponse.Tools(calls), new NullProjectExplorer(), (_, __) => { },
            null, null, null, null, null, default);

    [Fact]
    public async Task DeclareOutput_EmitsDeclareOp_AndReturnsId()
    {
        var r = await Run(FC("declare_output", new { content = "the plan", label = "Itinerary", contentType = "Markdown" }));
        var op = Assert.Single(r.OutputOps);
        Assert.Equal(OutputOpKind.Declare, op.Kind);
        Assert.NotNull(op.Declared);
        Assert.Equal(OutputKind.Document, op.Declared!.Kind);
        Assert.Equal("Itinerary", op.Declared.Label);
        Assert.Equal("the plan", op.Declared.Inline!.Content);
        Assert.Equal(op.Id, op.Declared.Id);
        Assert.Contains(op.Id, r.ToolOutputs.Single().Output);
    }

    [Fact]
    public async Task DeclareOutput_DefaultsContentTypeToMarkdown()
    {
        var r = await Run(FC("declare_output", new { content = "x" }));
        Assert.Equal(ArtifactContentType.Markdown, Assert.Single(r.OutputOps).Declared!.Inline!.ContentType);
    }

    [Fact]
    public async Task UpdateOutput_EmitsUpdateOp_WithProvidedFieldsOnly()
    {
        var r = await Run(FC("update_output", new { id = "abc", label = "Renamed" }));
        var op = Assert.Single(r.OutputOps);
        Assert.Equal(OutputOpKind.Update, op.Kind);
        Assert.Equal("abc", op.Id);
        Assert.Equal("Renamed", op.Label);
        Assert.Null(op.Inline);
        Assert.Equal("ok", r.ToolOutputs.Single().Output);
    }

    [Fact]
    public async Task RemoveOutput_EmitsRemoveOp()
    {
        var r = await Run(FC("remove_output", new { id = "abc" }));
        var op = Assert.Single(r.OutputOps);
        Assert.Equal(OutputOpKind.Remove, op.Kind);
        Assert.Equal("abc", op.Id);
        Assert.Equal("ok", r.ToolOutputs.Single().Output);
    }

    [Fact]
    public async Task UpdateOutput_WithContent_BuildsInlineWithDefaultMarkdown()
    {
        var r = await Run(FC("update_output", new { id = "abc", content = "new body" }));
        var op = Assert.Single(r.OutputOps);
        Assert.Equal(OutputOpKind.Update, op.Kind);
        Assert.NotNull(op.Inline);
        Assert.Equal("new body", op.Inline!.Content);
        Assert.Equal(ArtifactContentType.Markdown, op.Inline.ContentType);
        Assert.Null(op.Label);
    }

    [Fact]
    public async Task NonOutputRound_HasEmptyOutputOps()
    {
        var r = await Run(FC("round_intent", new { text = "go" }));
        Assert.Empty(r.OutputOps);
    }
}
