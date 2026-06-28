using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

/// <summary>The presenter is the single source of truth for what a USER sees when a run fails:
/// a short, class-specific message with a correlation reference (the runId) so support/devs can
/// pivot straight to App Insights. It never echoes the raw exception.</summary>
public class RunFailurePresenterTests
{
    private const string RunId = "a1b2c3d4-5555-6666-7777-888899990000";
    private const string ShortRef = "a1b2c3d4";   // first 8 chars of the runId

    [Theory]
    [InlineData(RunFailureClass.SandboxInfra, "workspace")]
    [InlineData(RunFailureClass.ModelProvider, "model service")]
    [InlineData(RunFailureClass.Exhaustion, "steps")]
    [InlineData(RunFailureClass.ReviewNotConverged, "agree")]
    [InlineData(RunFailureClass.UserTimeout, "input")]
    [InlineData(RunFailureClass.Unknown, "unexpectedly")]
    public void UserMessage_IsClassSpecific(RunFailureClass cls, string gist)
    {
        var msg = RunFailurePresenter.UserMessage(cls, RunId);
        Assert.Contains(gist, msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserMessage_AppendsShortCorrelationRef()
    {
        var msg = RunFailurePresenter.UserMessage(RunFailureClass.SandboxInfra, RunId);
        Assert.Contains(ShortRef, msg);
        Assert.DoesNotContain(RunId, msg);   // the FULL id is not dumped into the user copy — only the short ref
    }

    [Fact]
    public void UserMessage_NeverLeaksRawException()
    {
        // The presenter must never be handed a raw exception to echo; it maps purely from the class.
        var msg = RunFailurePresenter.UserMessage(RunFailureClass.ModelProvider, RunId);
        Assert.DoesNotContain("Exception", msg, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("403", msg);
    }

    [Fact]
    public void UserMessage_ToleratesShortRunId()
    {
        var msg = RunFailurePresenter.UserMessage(RunFailureClass.Unknown, "abc");
        Assert.Contains("abc", msg);   // shorter than 8 chars → use the whole thing, no crash
    }

    [Theory]
    [InlineData("The workspace sandbox could not be started", RunFailureClass.SandboxInfra)]
    [InlineData("Key Vault returned 403 Forbidden", RunFailureClass.SandboxInfra)]
    [InlineData("Foundry 429 Too Many Requests: ...", RunFailureClass.ModelProvider)]
    [InlineData("QA validation did not converge after 3 attempts", RunFailureClass.ReviewNotConverged)]
    [InlineData("reached the maximum of 24 rounds without completing", RunFailureClass.Exhaustion)]
    [InlineData("no response received before the wait timeout", RunFailureClass.UserTimeout)]
    [InlineData("some totally novel error nobody mapped", RunFailureClass.Unknown)]
    [InlineData(null, RunFailureClass.Unknown)]
    public void Classify_BestEffortFromRawReason(string? reason, RunFailureClass expected)
    {
        Assert.Equal(expected, RunFailurePresenter.Classify(reason));
    }
}
