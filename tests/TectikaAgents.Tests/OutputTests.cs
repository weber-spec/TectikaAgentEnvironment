using TectikaAgents.Core.Models;
using Xunit;

public class OutputTests
{
    [Fact]
    public void Valid_WhenOnlyInlineSet()
    {
        var o = new Output { Kind = OutputKind.Document, Inline = new InlineContent { Content = "hi" } };
        Assert.True(o.IsValid());
    }

    [Fact]
    public void Valid_WhenOnlyExternalSet()
    {
        var o = new Output { Kind = OutputKind.Code, External = new ExternalRef { Provider = "github" } };
        Assert.True(o.IsValid());
    }

    [Fact]
    public void Invalid_WhenBothSet()
    {
        var o = new Output { Inline = new InlineContent(), External = new ExternalRef() };
        Assert.False(o.IsValid());
    }

    [Fact]
    public void Invalid_WhenNeitherSet()
    {
        var o = new Output();
        Assert.False(o.IsValid());
    }

    [Fact]
    public void Output_HasGeneratedIdByDefault()
    {
        var o = new Output();
        Assert.False(string.IsNullOrWhiteSpace(o.Id));
    }
}
