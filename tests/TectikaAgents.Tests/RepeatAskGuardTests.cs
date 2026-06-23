using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Activities;
using Xunit;

namespace TectikaAgents.Tests;

/// <summary>QA S1 §2.2 — the repeat-ask guard converts request_human_input into autonomous continuation
/// once a task has paused for the human too many times.</summary>
public class RepeatAskGuardTests
{
    private static RoundOutcome Await(string callId) =>
        new(RoundKind.AwaitUser, null, Array.Empty<PriorToolOutput>(), callId,
            new PendingControl(PendingControlKind.HumanInput, "may I?"), null, null,
            Array.Empty<RoundToolCall>(), new TokenUsage(), "a");

    [Fact]
    public void BelowThreshold_LeavesAwaitUserUntouched()
    {
        var outcome = Await("call_q");
        var result = RepeatAskGuard.Apply(outcome, priorAskCount: 1, threshold: 2, out var fired);

        Assert.False(fired);
        Assert.Same(outcome, result);                  // returned unchanged
        Assert.Equal(RoundKind.AwaitUser, result.Kind);
    }

    [Fact]
    public void AtThreshold_ConvertsToContinue_WithNudgeAnsweringTheControlCall()
    {
        var result = RepeatAskGuard.Apply(Await("call_q"), priorAskCount: 2, threshold: 2, out var fired);

        Assert.True(fired);
        Assert.Equal(RoundKind.Continue, result.Kind);
        Assert.Null(result.OpenControlCallId);
        Assert.Null(result.Control);
        var injected = Assert.Single(result.NextToolOutputs);
        Assert.Equal("call_q", injected.CallId);       // answers the still-open control tool call
        Assert.Equal(RepeatAskGuard.Nudge, injected.Output);
    }

    [Fact]
    public void AboveThreshold_AlsoConverts()
    {
        var result = RepeatAskGuard.Apply(Await("c"), priorAskCount: 5, threshold: 2, out var fired);

        Assert.True(fired);
        Assert.Equal(RoundKind.Continue, result.Kind);
    }

    [Fact]
    public void PreservesExploreOutputs_WhenConverting()
    {
        var outcome = Await("call_q") with { NextToolOutputs = new[] { new PriorToolOutput("explore1", "data") } };
        var result = RepeatAskGuard.Apply(outcome, priorAskCount: 2, threshold: 2, out _);

        Assert.Equal(2, result.NextToolOutputs.Count);  // the explore output plus the injected nudge
        Assert.Contains(result.NextToolOutputs, o => o.CallId == "explore1");
        Assert.Contains(result.NextToolOutputs, o => o.Output == RepeatAskGuard.Nudge);
    }
}
