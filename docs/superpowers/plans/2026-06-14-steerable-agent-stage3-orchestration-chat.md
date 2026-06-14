# Steerable Agent Reasoning — Stage 3: Steerable Orchestration + Chat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make a task run a **steerable, Claude-Code-style loop**: the orchestrator drives the agent **one round at a time** (fine-grained Shape B), draining injected user messages between rounds; unify approval / interaction / free-form steering as one `user_message` external event; persist the round trace as `RunEvent`s; and expose chat as start-or-inject onto the same run.

**Architecture:** The Stage-2 in-activity tool-loop is decomposed so a **single round** (one Foundry `/responses` call + executing its explore tools) is one Durable **activity** (`RunAgentRoundActivity`). A new `SteerableAgentOrchestrator` owns the iteration: round → drain `user_message` (non-blocking, held-event-task pattern) → continue / await-user / finalize. The Foundry **conversation** carries history server-side; only pending tool outputs + injected text pass between rounds. The existing `TaskPipelineOrchestrator` stays for the legacy multi-step pipeline; new single-task runs route to the steerable orchestrator.

**Tech Stack:** C# / .NET 10, Durable Functions (.NET isolated), xUnit, Azure AI Foundry (verified contract), Cosmos, SSE.

**Spec:** `docs/superpowers/specs/2026-06-14-steerable-agent-reasoning-design.md`
**Depends on:** Stage 1 (RunEvent/PendingMessage/Prompt + containers) and Stage 2 (TectikaToolSchema, IProjectExplorer, AgentToolLoop, tool-loop runtime, budgeted ContextManager) — both merged.

**Decomposition (sub-stages, each independently testable):**
- **3a — Engine refactor:** extract a shared single-round executor; add `IAgentRuntime.RunRoundAsync`; build `SteerableAgentOrchestrator` + `RunAgentRoundActivity`; mock-drive the full loop. *No chat/UI yet.*
- **3b — Trace + events:** persist `RunEvent`s per round (round_started/intent, tool_call/result, artifact_written, user_message), widen `AgentEvent`, write the `runEvents`/SSE publisher, `GET /api/tasks/{id}/events`.
- **3c — Chat + unification:** `POST /api/tasks/{id}/chat` (start-or-inject → raise `user_message`), route run-start to the steerable orchestrator, unify approval/interaction with the same event.

---

## Verified contract reminders (do not deviate)
- One round = `POST /openai/v1/responses { input, agent_reference, conversation }`. `input` is the user/context string on the first round, or `[{type:"function_call_output", call_id, output}, …]` (optionally plus an injected user message item) on later rounds.
- Response → `function_call` items (execute, submit outputs next round) OR a final `message`.
- A control tool (`request_human_input`/`request_approval`/`request_revision`) is a `function_call` whose **output is supplied by the human** — submit the user's reply as that call_id's `function_call_output` on the resuming round. This is what unifies steering + interaction + approval.

---

## Sub-stage 3a — Engine refactor (orchestrator drives rounds)

### File Structure (3a)
- **Modify** `src/agentruntime/AgentToolLoop.cs` — extract `RoundExecutor.ExecuteOneRoundAsync(...)` (the per-iteration body) so both the in-proc loop and the new round activity share one implementation.
- **Create** `src/core/TectikaAgents.Core/Models/RoundContracts.cs` — `RoundRequest`, `RoundOutcome`, `RoundKind`, `PriorToolOutput`, `RoundToolCall`.
- **Modify** `src/core/TectikaAgents.Core/Interfaces/IAgentRuntime.cs` — add `RunRoundAsync`.
- **Modify** `src/agentruntime/FoundryAgentRuntime.cs` — implement `RunRoundAsync` (one Foundry call + explore-tool execution via the shared executor). Keep `RunTurnAsync` for the non-steerable path (delegates to the loop).
- **Modify** `src/agentruntime/MockAgentRuntime.cs` — implement `RunRoundAsync` (scriptable: first round → optional tool call; next → final).
- **Create** `src/workflows/Activities/RunAgentRoundActivity.cs` — load role/task/board + explorer, on round 0 assemble context, call `RunRoundAsync`, return a serializable round result; (artifact write + RunEvent persistence wired in 3b).
- **Create** `src/workflows/Orchestrators/SteerableAgentOrchestrator.cs` — the round loop with non-blocking `user_message` drain.
- **Tests:** `RoundExecutorTests` (single round: explore tool executed, control captured, final passthrough), `SteerableOrchestratorMockTests` (drive several rounds via a mock runtime; assert finalize + await-user transitions). Orchestrator tests use the Durable test harness if available, else a thin extracted `RunLoopAsync(IRoundDriver)` core that is unit-testable without the Durable runtime.

### Key contracts (3a) — `RoundContracts.cs`
```csharp
namespace TectikaAgents.Core.Models;

public enum RoundKind { Continue, Final, AwaitUser }

/// <summary>Inputs for ONE agent round. ThreadId is the Foundry conversation (carries history).</summary>
public sealed record RoundRequest(
    AgentRole Role, AgentTask Task, string ThreadId,
    string? UserInput,                                  // round-0 context / injected steering / human control-answer
    IReadOnlyList<PriorToolOutput> PendingToolOutputs,  // function_call_outputs to submit from the previous round
    int MaxCompletionTokens, string RunId, int Round);

public sealed record PriorToolOutput(string CallId, string Output);
public sealed record RoundToolCall(string Name, string ArgsSummary, string ResultSummary);

public sealed record RoundOutcome(
    RoundKind Kind,
    string? FinalText,
    IReadOnlyList<PriorToolOutput> NextToolOutputs,     // explore outputs computed this round (submit next)
    string? OpenControlCallId,                          // AwaitUser: the request_* call_id awaiting a human output
    PendingControl? Control,
    string? RoundIntent, string? BriefUpdate,
    IReadOnlyList<RoundToolCall> ToolCalls,             // for the RunEvent trace (3b)
    TokenUsage Usage, string CompletionId, string? Error = null);
```

### `RunRoundAsync` on `IAgentRuntime`
```csharp
/// <summary>Run exactly ONE model⇄tool round. Submits any pending tool outputs (+ optional user
/// input) to Foundry, executes returned explore tools via the explorer, and returns the round's
/// outcome. The orchestrator owns iteration (fine-grained Shape B steering).</summary>
Task<RoundOutcome> RunRoundAsync(RoundRequest req, IProjectExplorer explorer, CancellationToken ct = default);
```

### `SteerableAgentOrchestrator` (round loop, replay-safe)
```text
input: SteerableRunInput(RunId, TaskId, BoardId, TenantId, AgentRoleId, SeedMessage?)
mark run Running (activity)
string? userInput = SeedMessage   // round 0 also assembles task context inside the activity
pending = []
Task<string> msgWait = context.WaitForExternalEvent<string>("user_message")   // held-event-task
for round in 0..MaxRounds:
    outcome = await CallActivity<RoundActivityResult>(RunAgentRoundActivity,
                  new RoundActivityInput(RunId, TaskId, BoardId, TenantId, AgentRoleId, round, userInput, pending))
    // drain steering without blocking
    string? injected = null
    if (msgWait.IsCompleted) { injected = msgWait.Result; msgWait = context.WaitForExternalEvent<string>("user_message"); }
    switch outcome.Kind:
      Final     -> mark Completed (activity); break
      AwaitUser -> mark AwaitingInteraction (activity); write interaction/approval doc (activity)
                   reply = injected ?? await msgWait; if used msgWait, re-arm it
                   mark Running; userInput = null
                   pending = [ new PriorToolOutput(outcome.OpenControlCallId!, reply) ]
      Continue  -> userInput = injected            // fold steering into the next round (may be null)
                   pending = outcome.NextToolOutputs
mark Failed if MaxRounds exhausted without Final
```
> Replay-safety: all IO is in activities; `msgWait.IsCompleted`/`.Result` on the durable external-event task is replay-safe (Durable replays the event deterministically). Keep exactly one outstanding `WaitForExternalEvent("user_message")` at a time.

### Tasks (3a)
- [ ] **T1:** Extract `RoundExecutor.ExecuteOneRoundAsync(sendOneCall, explorer, pending, userInput, onToolCall)` from `AgentToolLoop`; refactor `AgentToolLoop.RunAsync` to call it in a loop. Tests: existing `AgentToolLoopTests` still green; new `RoundExecutorTests` (explore tool executed + outputs returned; control tool → `AwaitUser` + `OpenControlCallId`; no tool calls → `Final`).
- [ ] **T2:** Add `RoundContracts.cs` + `RunRoundAsync` to `IAgentRuntime`; implement in `MockAgentRuntime` (scriptable) and `FoundryAgentRuntime` (one `/responses` call via the shared executor). Tests: `MockAgentRuntime.RunRoundAsync` returns Continue→Final across two calls.
- [ ] **T3:** `RunAgentRoundActivity` — load role/task/board, `BoardProjectExplorer`, round-0 context via `ContextManager`, call `RunRoundAsync`, return `RoundActivityResult`. (Artifact write deferred to 3b; for 3a, on `Final` it writes the artifact reusing the Stage-2 artifact code path so the loop is end-to-end.) Test: with `MockAgentRuntime`, returns Final + an artifact id.
- [ ] **T4:** `SteerableAgentOrchestrator` — implement the loop above. Extract the pure loop body into `SteerableRunCore.RunLoopAsync(IRoundDriver driver)` so it unit-tests without the Durable host; the orchestrator is a thin adapter. Tests `SteerableOrchestratorMockTests`: (a) two Continue rounds then Final → Completed; (b) a round returns AwaitUser → blocks → an injected message resumes with the reply as the control output; (c) a Continue round with a queued message folds it into the next round's `userInput`.
- [ ] **T5:** Build + full unit sweep green; commit per task.

---

## Sub-stage 3b — Round trace (`RunEvent`) + events  *(outline — code-complete when 3a lands)*

- Add `WorkflowCosmosService.CreateRunEventAsync(RunEvent)` + `GetRunEventsAsync(taskId, sinceRound?)`.
- `RunAgentRoundActivity` emits, per round: `round_started` (Title = `RoundIntent`), one `tool_call`+`tool_result` per `RoundToolCall` (ParentId = the round_started id), `artifact_written` on Final, `agent_message` for final text. Each persisted `RunEvent` is **also** published over SSE (same object) — extend `AgentEvent` with `Round`/`ParentId`/`Kind`/`Title`/`ToolName`/`ToolArgsSummary`/`ResultSummary` and add `WorkflowEventPublisher.PublishRunEventAsync(RunEvent)`.
- API: `GET /api/tasks/{taskId}/events?sinceRound=` → `GetRunEventsAsync` (replay for the Activity tab).
- Tests: round activity persists the expected RunEvent tree (parent round + child tool calls) using an in-memory cosmos fake; `/events` returns them ordered.

## Sub-stage 3c — Chat + unification  *(outline — code-complete when 3b lands)*

- `POST /api/tasks/{taskId}/chat { text }`: if `task.WorkflowRunId` is null/!running → start `SteerableAgentOrchestrator` seeded with `text` (via `HttpTrigger` new route `pipelines/steerable/start`); else raise `user_message` to the instance (`RaiseEventAsync(instanceId, "user_message", text)`, mirrored to a `PendingMessage` for trace). Always persist a `user_message` RunEvent immediately.
- Route normal run-start (`RunStartService`) to the steerable orchestrator for single-task runs.
- Approvals/interactions: the existing approve/respond endpoints additionally raise `user_message` carrying the decision/answer (the orchestrator’s AwaitUser path consumes it) — one mechanism.
- Tests: chat-when-idle starts a run (mock durable client); chat-when-running raises the event; approval reply flows through AwaitUser.

---

## Self-Review (3a focus)
**Spec coverage:** fine-grained Shape B (orchestrator drives rounds, non-blocking steer drain) → T4; single-round runtime → T1-T3; control tools unify with steering via `OpenControlCallId` → T1/T4; mock-testable end to end → T3/T4. 3b/3c carry RunEvent persistence, chat, and the API surface (outlined; coded when 3a lands). ✓
**Placeholders:** 3a tasks specify concrete contracts + test cases; 3b/3c are explicitly outlines to be expanded after 3a (not placeholders within an executable task). ✓
**Type consistency:** `RoundOutcome`/`RoundKind`/`PriorToolOutput`/`RoundToolCall` defined once (RoundContracts) and consumed by runtime + activity + orchestrator; `OpenControlCallId` + `PendingControl` (from Stage 2) used consistently in the AwaitUser path. ✓
