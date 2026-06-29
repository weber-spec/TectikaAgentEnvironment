using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Activities;
using Xunit;

namespace TectikaAgents.Tests;

/// <summary>The pure loop heuristic: does a window of recent rounds look like the agent is stuck repeating
/// the same tool calls without progress (vs. a legitimate long task that merely filled its context)?</summary>
public class LoopProgressGuardTests
{
    private static RoundOutcome Round(RoundToolCall[] tools, OutputOp[]? ops = null, string? brief = null) =>
        new(RoundKind.Continue, null, Array.Empty<PriorToolOutput>(), null, null, null, brief,
            tools, new TokenUsage(), "c", OutputOps: ops);

    private static RoundToolCall Tool(string name, string args) => new(name, args, "ok");

    [Fact]
    public void Signature_JoinsToolCallsAndDetectsProgressFromOutputOps()
    {
        var sig = LoopProgressGuard.Signature(Round(
            new[] { Tool("read_file", "a.cs"), Tool("run_command", "build") },
            ops: new[] { new OutputOp(OutputOpKind.Declare, "o1") }));

        Assert.Equal("read_file|a.cs;run_command|build", sig.ToolSig);
        Assert.True(sig.MadeProgress);
    }

    [Fact]
    public void Signature_DetectsProgressFromBriefUpdate()
    {
        var sig = LoopProgressGuard.Signature(Round(new[] { Tool("read_file", "a.cs") }, brief: "made a note"));
        Assert.True(sig.MadeProgress);
    }

    [Fact]
    public void Signature_NoProgress_WhenNoOpsOrBrief()
    {
        var sig = LoopProgressGuard.Signature(Round(new[] { Tool("read_file", "a.cs") }));
        Assert.False(sig.MadeProgress);
        Assert.Equal("read_file|a.cs", sig.ToolSig);
    }

    [Fact]
    public void LooksStuck_True_WhenWindowRepeatsSameToolSigWithoutProgress()
    {
        var sigs = new[]
        {
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
        };
        Assert.True(LoopProgressGuard.LooksStuck(sigs, window: 3));
    }

    [Fact]
    public void LooksStuck_False_WhenAnyRoundMadeProgress()
    {
        var sigs = new[]
        {
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") }, brief: "progress")),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
        };
        Assert.False(LoopProgressGuard.LooksStuck(sigs, window: 3));
    }

    [Fact]
    public void LooksStuck_False_WhenToolSigsDiffer()
    {
        var sigs = new[]
        {
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "test") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
        };
        Assert.False(LoopProgressGuard.LooksStuck(sigs, window: 3));
    }

    [Fact]
    public void LooksStuck_False_OnEmptyToolSig()
    {
        // Pure text/thinking rounds (no tool calls) must never be flagged as a loop.
        var sigs = new[]
        {
            LoopProgressGuard.Signature(Round(Array.Empty<RoundToolCall>())),
            LoopProgressGuard.Signature(Round(Array.Empty<RoundToolCall>())),
            LoopProgressGuard.Signature(Round(Array.Empty<RoundToolCall>())),
        };
        Assert.False(LoopProgressGuard.LooksStuck(sigs, window: 3));
    }

    [Fact]
    public void LooksStuck_False_BelowWindowSize()
    {
        var sigs = new[]
        {
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
        };
        Assert.False(LoopProgressGuard.LooksStuck(sigs, window: 3));
    }

    [Fact]
    public void LooksStuck_OnlyConsidersTheMostRecentWindow()
    {
        // Earlier diverse rounds should not prevent detecting a stuck tail.
        var sigs = new[]
        {
            LoopProgressGuard.Signature(Round(new[] { Tool("get_task", "t1") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
            LoopProgressGuard.Signature(Round(new[] { Tool("run_command", "build") })),
        };
        Assert.True(LoopProgressGuard.LooksStuck(sigs, window: 3));
    }
}
