using TectikaAgents.AgentRuntime;
using Xunit;

public class FoundryConversationHealTests
{
    // The exact 400 body shape Foundry returned in production (see App Insights trace).
    private const string RealBody =
        "{ \"error\": { \"message\": \"No tool output found for function call call_oiv3mfXoOwiP2LOP9E7HMT6E.\", " +
        "\"type\": \"invalid_request_error\", \"param\": \"input\", \"code\": null } }";

    [Fact]
    public void ParsesDanglingCallId_FromRealFoundry400Body()
    {
        Assert.Equal("call_oiv3mfXoOwiP2LOP9E7HMT6E",
            FoundryConversationHeal.ParseMissingToolCallId(RealBody));
    }

    [Theory]
    [InlineData("No tool output found for function call call_abc123.", "call_abc123")]
    [InlineData("no tool output found for function call call_XYZ", "call_XYZ")]   // case-insensitive, no period
    public void ParsesDanglingCallId_AcrossFormatting(string body, string expected)
    {
        Assert.Equal(expected, FoundryConversationHeal.ParseMissingToolCallId(body));
    }

    [Theory]
    [InlineData("{ \"error\": { \"message\": \"Rate limit exceeded\" } }")]
    [InlineData("Internal server error")]
    [InlineData("")]
    [InlineData(null)]
    public void ReturnsNull_WhenNotAMissingToolOutputError(string? body)
    {
        Assert.Null(FoundryConversationHeal.ParseMissingToolCallId(body));
    }
}
