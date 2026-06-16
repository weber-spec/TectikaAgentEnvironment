using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class RoundTitleTests
{
    private static RoundOutcome Out(RoundKind kind, string? finalText, string? intent, params string[] tools) =>
        new(kind, finalText, [], null, null, intent, null,
            tools.Select(t => new RoundToolCall(t, "", "ok")).ToList(), new TokenUsage(), "c1");

    [Fact]
    public void Uses_round_intent_when_present()
    {
        var o = Out(RoundKind.Continue, null, "Gathering context", "get_board_overview");
        Assert.Equal("Gathering context", RoundTitle.Synthesize(o, 0));
    }

    [Fact]
    public void Final_uses_answer_snippet()
    {
        var o = Out(RoundKind.Final, "Added retry logic to the uploader.", null);
        Assert.Equal("Added retry logic to the uploader.", RoundTitle.Synthesize(o, 3));
    }

    [Fact]
    public void Synthesizes_from_tools_skipping_meta()
    {
        var o = Out(RoundKind.Continue, null, null, "round_intent", "get_board_overview", "search_tasks");
        Assert.Equal("Read board, searched board", RoundTitle.Synthesize(o, 0));
    }

    [Fact]
    public void Falls_back_to_round_number_when_nothing_descriptive()
    {
        var o = Out(RoundKind.Continue, null, null);
        Assert.Equal("Round 2", RoundTitle.Synthesize(o, 1));
    }
}
