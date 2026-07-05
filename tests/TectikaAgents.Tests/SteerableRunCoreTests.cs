using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Activities;
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
        public int CompactCalls;          // how many times CompactContextAsync fired
        public string? CompactBrief;      // brief CompactContextAsync returns (null = compaction failed)
        public string? ExhaustedReason;   // set when OnExhaustedAsync fires
        public RunFailureClass? ExhaustedClass;   // the class passed alongside the exhaustion reason

        public FakeDriver(IEnumerable<RoundOutcome> script, IEnumerable<string>? userMessages = null,
            string? steer = null, string? compactBrief = null)
        { _script = new(script); _userMessages = new(userMessages ?? Array.Empty<string>()); _steer = steer; CompactBrief = compactBrief; }

        public Task<RoundOutcome> RunRoundAsync(int round, string? userInput, IReadOnlyList<PriorToolOutput> pending)
        { Calls.Add((round, userInput, pending.Count)); return Task.FromResult(_script.Dequeue()); }
        // Returns null when no message is queued — models the AwaitUser wait timing out.
        public Task<string?> WaitForUserMessageAsync() { WaitCalls++; return Task.FromResult(_userMessages.Count > 0 ? _userMessages.Dequeue() : null); }
        public string? TryDrainUserMessage() { var s = _steer; _steer = null; return s; }
        public Task OnStateAsync(SteerableState state, RoundOutcome? last) { States.Add(state); return Task.CompletedTask; }
        public Task<string?> CompactContextAsync() { CompactCalls++; return Task.FromResult(CompactBrief); }
        public Task OnExhaustedAsync(string reason, RunFailureClass cls, RoundOutcome? last) { ExhaustedReason = reason; ExhaustedClass = cls; return Task.CompletedTask; }
    }

    private static RoundOutcome Cont(params PriorToolOutput[] next) =>
        new(RoundKind.Continue, null, next, null, null, null, null, Array.Empty<RoundToolCall>(), new TokenUsage(), "c");

    /// <summary>A Continue round reporting <paramref name="inputTokens"/> of context plus a repeated tool
    /// signature with NO progress — the loop fingerprint for budget+stuck tests.</summary>
    private static RoundOutcome ContUsage(int inputTokens) =>
        new(RoundKind.Continue, null, Array.Empty<PriorToolOutput>(), null, null, null, null,
            new[] { new RoundToolCall("run_command", "build", "ok") }, new TokenUsage { Input = inputTokens }, "c");

    /// <summary>A Continue round reporting context usage but WITH progress (a brief update) — not a loop.</summary>
    private static RoundOutcome ContProgress(int inputTokens) =>
        new(RoundKind.Continue, null, Array.Empty<PriorToolOutput>(), null, null, null, "made progress",
            new[] { new RoundToolCall("run_command", "build", "ok") }, new TokenUsage { Input = inputTokens }, "c");
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
        Assert.Equal(RunFailureClass.Exhaustion, d.ExhaustedClass);   // round-cap → Exhaustion class
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
        Assert.Equal(RunFailureClass.UserTimeout, d.ExhaustedClass);   // AwaitUser timeout → UserTimeout class
    }

    [Fact]
    public async Task BudgetHit_LooksStuck_InjectsDiagnosticPrompt_ThenFinalCompletes()
    {
        // 3 identical no-progress rounds fill the budget on round 2 → diagnostic prompt injected for round 3;
        // the agent then stops with Final → graceful completion.
        var d = new FakeDriver(new[] { ContUsage(10), ContUsage(50), ContUsage(150), Final() });
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 20, contextBudgetTokens: 100, loopWindow: 3);

        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(4, d.Calls.Count);
        Assert.Contains(LoopProgressGuard.DiagnosticPrompt, d.Calls[3].userInput);  // prompt fed to round 3
        Assert.Equal(0, d.CompactCalls);                                            // stuck path does NOT compact
    }

    [Fact]
    public async Task BudgetHit_DiagnosticRoundContinues_Compacts_AndKeepsGoing()
    {
        // Budget hit + stuck → diagnostic prompt; agent answers Continue (declares not-stuck) → compact +
        // re-seed the returned brief, then Final.
        var d = new FakeDriver(new[] { ContUsage(10), ContUsage(50), ContUsage(150), ContUsage(10), Final() },
            compactBrief: "summary-brief");
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 20, contextBudgetTokens: 100, loopWindow: 3);

        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(1, d.CompactCalls);                       // compacted once after the not-stuck answer
        Assert.Equal("summary-brief", d.Calls[4].userInput);   // brief re-seeded onto the fresh thread
        Assert.Equal(0, d.Calls[4].pending);                   // pending cleared (old thread discarded)
    }

    [Fact]
    public async Task BudgetHit_NotALoop_CompactsDirectly_AndReseeds()
    {
        // Each round makes progress (differing/progressing signatures) so it is NOT a loop; the budget breach
        // compacts directly without a diagnostic round.
        var d = new FakeDriver(new[] { ContProgress(150), Final() }, compactBrief: "brief");
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 20, contextBudgetTokens: 100, loopWindow: 3);

        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(1, d.CompactCalls);
        Assert.Equal("brief", d.Calls[1].userInput);   // round 1 re-seeded from compaction
        Assert.Equal(0, d.Calls[1].pending);
    }

    [Fact]
    public async Task BudgetHit_CompactionReturnsNull_ContinuesWithoutCrashing()
    {
        var d = new FakeDriver(new[] { ContProgress(150), Final() }, compactBrief: null);  // compaction failed
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 20, contextBudgetTokens: 100, loopWindow: 3);

        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(1, d.CompactCalls);
        Assert.Null(d.Calls[1].userInput);             // no brief, no steering → null seed, but no crash
    }

    [Fact]
    public async Task NoBudgetSet_BehavesLikeBefore()
    {
        // Default contextBudgetTokens (int.MaxValue) never trips, even with high reported usage.
        var d = new FakeDriver(new[] { ContUsage(999_999), Final() });
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(0, d.CompactCalls);
    }
}
