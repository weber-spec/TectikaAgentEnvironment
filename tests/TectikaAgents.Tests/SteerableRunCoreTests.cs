using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Orchestrators;
using Xunit;

public class SteerableRunCoreTests
{
    /// <summary>Scripted driver: returns queued outcomes; records what each round received.</summary>
    private sealed class FakeDriver : IRoundDriver
    {
        private readonly Queue<RoundOutcome> _script;
        private readonly Queue<string> _userMessages;     // delivered by WaitForUserMessageAsync
        private string? _steer;                            // drained non-blocking once
        public readonly List<(int round, string? userInput, int pending)> Calls = new();
        public readonly List<SteerableState> States = new();
        public int WaitCalls;
        public string? ExhaustedReason;   // set when OnExhaustedAsync fires

        public FakeDriver(IEnumerable<RoundOutcome> script, IEnumerable<string>? userMessages = null, string? steer = null)
        { _script = new(script); _userMessages = new(userMessages ?? Array.Empty<string>()); _steer = steer; }

        public Task<RoundOutcome> RunRoundAsync(int round, string? userInput, IReadOnlyList<PriorToolOutput> pending)
        { Calls.Add((round, userInput, pending.Count)); return Task.FromResult(_script.Dequeue()); }
        // Returns null when no message is queued — models the AwaitUser wait timing out.
        public Task<string?> WaitForUserMessageAsync() { WaitCalls++; return Task.FromResult(_userMessages.Count > 0 ? _userMessages.Dequeue() : null); }
        public string? TryDrainUserMessage() { var s = _steer; _steer = null; return s; }
        public Task OnStateAsync(SteerableState state, RoundOutcome? last) { States.Add(state); return Task.CompletedTask; }
        public Task OnExhaustedAsync(string reason, RoundOutcome? last) { ExhaustedReason = reason; return Task.CompletedTask; }
    }

    private static RoundOutcome Cont(params PriorToolOutput[] next) =>
        new(RoundKind.Continue, null, next, null, null, null, null, Array.Empty<RoundToolCall>(), new TokenUsage(), "c");
    private static RoundOutcome Final() =>
        new(RoundKind.Final, "done", Array.Empty<PriorToolOutput>(), null, null, null, null, Array.Empty<RoundToolCall>(), new TokenUsage(), "f");
    private static RoundOutcome Await(string callId) =>
        new(RoundKind.AwaitUser, null, Array.Empty<PriorToolOutput>(), callId, new PendingControl(PendingControlKind.HumanInput, "?"), null, null, Array.Empty<RoundToolCall>(), new TokenUsage(), "a");
    private static RoundOutcome Revision(string reason) =>
        new(RoundKind.NeedsRevision, null, Array.Empty<PriorToolOutput>(), "call_rev", new PendingControl(PendingControlKind.Revision, reason), null, null, Array.Empty<RoundToolCall>(), new TokenUsage(), "rev");

    [Fact]
    public async Task TwoContinues_ThenFinal_Completes()
    {
        var d = new FakeDriver(new[] { Cont(new PriorToolOutput("x", "ok")), Cont(), Final() });
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(3, d.Calls.Count);
        Assert.Equal("go", d.Calls[0].userInput);           // seed delivered to round 0
        Assert.Equal(1, d.Calls[1].pending);                // round 0's tool output carried to round 1
    }

    [Fact]
    public async Task AwaitUser_BlocksForReply_ThenSubmitsItAsControlOutput()
    {
        var d = new FakeDriver(new[] { Await("call_q"), Final() }, userMessages: new[] { "the answer" });
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(1, d.WaitCalls);                        // blocked once
        Assert.Equal(1, d.Calls[1].pending);                 // reply submitted as the control output
        Assert.Null(d.Calls[1].userInput);
    }

    [Fact]
    public async Task QueuedSteering_IsFoldedIntoNextRound()
    {
        var d = new FakeDriver(new[] { Cont(), Final() }, steer: "change direction");
        await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal("change direction", d.Calls[1].userInput);   // drained after round 0, fed to round 1
    }

    [Fact]
    public async Task AwaitUser_WithQueuedSteer_DoesNotBlock()
    {
        var d = new FakeDriver(new[] { Await("call_q"), Final() }, steer: "already here");
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(0, d.WaitCalls);                        // injected message used instead of blocking
    }

    [Fact]
    public async Task Revision_TerminatesAsNeedsRevision_NotAwaitOrExhausted()
    {
        var d = new FakeDriver(new[] { Revision("fix the schema") });
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(SteerableState.NeedsRevision, state);
        Assert.Contains(SteerableState.NeedsRevision, d.States);
        Assert.Equal(0, d.WaitCalls);          // does NOT pause for a human
        Assert.Null(d.ExhaustedReason);        // clean terminal, not a round-cap finalize
    }

    [Fact]
    public async Task MaxRoundsHit_FinalizesAndFails_NotSilent()
    {
        var d = new FakeDriver(new[] { Cont(), Cont() });    // never reaches Final
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 2);
        Assert.Equal(SteerableState.Failed, state);
        Assert.NotNull(d.ExhaustedReason);                   // partial work finalized, not stranded
        Assert.Contains("maximum", d.ExhaustedReason!);
    }

    [Fact]
    public async Task AwaitUser_NoReplyBeforeTimeout_Finalizes()
    {
        var d = new FakeDriver(new[] { Await("call_q") });   // no queued user messages → wait returns null
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(SteerableState.Failed, state);
        Assert.Equal(1, d.WaitCalls);                        // blocked once, then timed out
        Assert.NotNull(d.ExhaustedReason);
        Assert.Contains("timeout", d.ExhaustedReason!);
    }
}
