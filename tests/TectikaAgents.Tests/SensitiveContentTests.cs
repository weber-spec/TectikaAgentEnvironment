using TectikaAgents.Core.Observability;
using Xunit;

namespace TectikaAgents.Tests;

public class SensitiveContentTests
{
    [Fact]
    public void Format_ReturnsContent_WhenLoggingEnabled()
    {
        Assert.Equal("secret prompt", SensitiveContent.Format("secret prompt", logSensitive: true));
    }

    [Fact]
    public void Format_RedactsWithLength_WhenLoggingDisabledAndContentPresent()
    {
        Assert.Equal("[redacted](6 chars)", SensitiveContent.Format("abcdef", logSensitive: false));
    }

    [Fact]
    public void Format_ReturnsBareRedacted_WhenLoggingDisabledAndContentEmpty()
    {
        Assert.Equal("[redacted]", SensitiveContent.Format("", logSensitive: false));
        Assert.Equal("[redacted]", SensitiveContent.Format(null, logSensitive: false));
    }

    [Fact]
    public void Format_ReturnsEmpty_WhenLoggingEnabledAndContentNull()
    {
        Assert.Equal("", SensitiveContent.Format(null, logSensitive: true));
    }
}
