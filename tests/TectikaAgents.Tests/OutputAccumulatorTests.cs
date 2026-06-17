using TectikaAgents.Core.Models;
using Xunit;

public class OutputAccumulatorTests
{
    private static Output Doc(string id, string content, string? label = null) => new()
    {
        Id = id, Kind = OutputKind.Document, Label = label,
        Inline = new InlineContent { ContentType = ArtifactContentType.Markdown, Content = content },
    };

    [Fact]
    public void Declare_AppendsOutput()
    {
        var result = OutputAccumulator.Apply([], [new OutputOp(OutputOpKind.Declare, "a", Declared: Doc("a", "hi"))]);
        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
        Assert.Equal("hi", result[0].Inline!.Content);
    }

    [Fact]
    public void Update_ChangesLabelAndInlineOfMatchingId()
    {
        var current = new List<Output> { Doc("a", "old", "Old") };
        var result = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Update, "a",
            Label: "New", Inline: new InlineContent { ContentType = ArtifactContentType.Json, Content = "new" })]);
        Assert.Single(result);
        Assert.Equal("New", result[0].Label);
        Assert.Equal("new", result[0].Inline!.Content);
        Assert.Equal(ArtifactContentType.Json, result[0].Inline!.ContentType);
    }

    [Fact]
    public void Update_LeavesOmittedFieldsUnchanged()
    {
        var current = new List<Output> { Doc("a", "keep", "Keep") };
        var result = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Update, "a", Label: "Renamed")]);
        Assert.Equal("Renamed", result[0].Label);
        Assert.Equal("keep", result[0].Inline!.Content);
    }

    [Fact]
    public void Update_UnknownId_IsNoOp()
    {
        var current = new List<Output> { Doc("a", "hi") };
        var result = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Update, "zzz", Label: "x")]);
        Assert.Single(result);
        Assert.Null(result[0].Label);
    }

    [Fact]
    public void Remove_DropsMatchingId_UnknownIsNoOp()
    {
        var current = new List<Output> { Doc("a", "1"), Doc("b", "2") };
        var afterRemove = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Remove, "a")]);
        Assert.Single(afterRemove);
        Assert.Equal("b", afterRemove[0].Id);

        var afterUnknown = OutputAccumulator.Apply(afterRemove, [new OutputOp(OutputOpKind.Remove, "nope")]);
        Assert.Single(afterUnknown);
    }

    [Fact]
    public void Apply_DoesNotMutateInputList()
    {
        var current = new List<Output> { Doc("a", "1") };
        _ = OutputAccumulator.Apply(current, [new OutputOp(OutputOpKind.Declare, "b", Declared: Doc("b", "2"))]);
        Assert.Single(current);
    }

    [Fact]
    public void Sequence_DeclareDeclareUpdateRemove()
    {
        var ops = new List<OutputOp>
        {
            new(OutputOpKind.Declare, "a", Declared: Doc("a", "A")),
            new(OutputOpKind.Declare, "b", Declared: Doc("b", "B")),
            new(OutputOpKind.Update, "a", Label: "Alpha"),
            new(OutputOpKind.Remove, "b"),
        };
        var result = OutputAccumulator.Apply([], ops);
        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
        Assert.Equal("Alpha", result[0].Label);
    }
}
