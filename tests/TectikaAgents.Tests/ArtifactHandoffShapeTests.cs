using TectikaAgents.Core.Models;
using Xunit;

public class ArtifactHandoffShapeTests
{
    [Fact]
    public void Legacy_DerivesSingleDocumentOutput()
    {
        var a = new Artifact { ContentType = ArtifactContentType.Markdown, Content = "## Plan\nDay 1: arrive" };

        a.EnsureHandoffShape();

        Assert.Single(a.Outputs);
        var o = a.Outputs[0];
        Assert.Equal(OutputKind.Document, o.Kind);
        Assert.NotNull(o.Inline);
        Assert.Equal(ArtifactContentType.Markdown, o.Inline!.ContentType);
        Assert.Equal("## Plan\nDay 1: arrive", o.Inline.Content);
    }

    [Fact]
    public void Legacy_DerivesSummaryFromFirstMeaningfulLine()
    {
        var a = new Artifact { Content = "## Plan\nDay 1: arrive", Summary = null };

        a.EnsureHandoffShape();

        Assert.Equal("Plan", a.Summary);
    }

    [Fact]
    public void Legacy_KeepsExistingSummaryWhenPresent()
    {
        var a = new Artifact { Content = "## Plan", Summary = "Trip itinerary" };

        a.EnsureHandoffShape();

        Assert.Equal("Trip itinerary", a.Summary);
    }

    [Fact]
    public void New_ArtifactWithOutputsIsLeftUnchanged()
    {
        var a = new Artifact
        {
            Summary = "Added checkout",
            Outputs = [new Output { Kind = OutputKind.Document, Inline = new InlineContent { Content = "body" } }],
        };

        a.EnsureHandoffShape();

        Assert.Single(a.Outputs);
        Assert.Equal("Added checkout", a.Summary);
    }

    [Fact]
    public void Empty_ArtifactProducesNoOutputsAndEmptySummary()
    {
        var a = new Artifact { Content = "", Summary = null };

        a.EnsureHandoffShape();

        Assert.Empty(a.Outputs);
        Assert.Equal("", a.Summary);
    }

    [Fact]
    public void Legacy_SkipsSeparatorOnlyLinesWhenDerivingSummary()
    {
        var a = new Artifact { Content = "---\nReal heading", Summary = null };

        a.EnsureHandoffShape();

        Assert.Equal("Real heading", a.Summary);
    }

    [Fact]
    public void EnsureHandoffShape_IsIdempotent()
    {
        var a = new Artifact { ContentType = ArtifactContentType.Markdown, Content = "## Plan\nDay 1" };

        a.EnsureHandoffShape();
        var summaryAfterFirst = a.Summary;
        a.EnsureHandoffShape();

        Assert.Single(a.Outputs);
        Assert.Equal(summaryAfterFirst, a.Summary);
    }

    [Fact]
    public void Legacy_TruncatesLongSummaryWithEllipsis()
    {
        var a = new Artifact { Content = new string('a', 300), Summary = null };

        a.EnsureHandoffShape();

        Assert.Equal(201, a.Summary!.Length); // 200 chars + the "…" ellipsis
        Assert.EndsWith("…", a.Summary);
    }
}
