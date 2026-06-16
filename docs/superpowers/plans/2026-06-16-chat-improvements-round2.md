# Chat Improvements (Round 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the redundant Updates tab; keep the chat live (real-time push + poll backstop); make in-chat activity rows collapsible; give Activity-tab rounds descriptive titles instead of "Round n".

**Architecture:** Mostly frontend changes to `ItemPanel.tsx`, plus a one-line real-time SSE broadcast of user messages in the API, plus a synthesized round-title helper in the workflows `RunEventFactory`.

**Tech Stack:** .NET (C#, xUnit `tests/TectikaAgents.Tests`), Next.js 16 + React + Tailwind (`src/web/tectika-board`). No JS test runner — web verified via `tsc` + `eslint` + `next build --webpack`.

**Spec:** `docs/superpowers/specs/2026-06-16-chat-improvements-round2-design.md`

**Branch:** `feat/chat-improvements-2` (already created off the `feat/agent-requests-hitl` tip; the spec commit is already here).

---

## File Structure

- **workflows** `src/workflows/Services/RunEventFactory.cs` — add a `public static class RoundTitle` helper + use it for the `RoundStarted` title (Item 4).
- **tests** `tests/TectikaAgents.Tests/RoundTitleTests.cs` — unit tests for the helper (Item 4).
- **api** `src/api/TectikaAgents.Api/Services/ChatService.cs` — broadcast the echoed user message over SSE (Item 2a).
- **web** `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` — remove Updates (Item 1), poll in `useRunEvents` (Item 2b), collapsible `HistoryStep` (Item 3).
- **web** `src/web/tectika-board/src/lib/board-context.tsx` — remove the now-dead `comments`/`addComment` from the context (Item 1).

Frontend Items 1, 2b, 3 all edit `ItemPanel.tsx`, so their tasks run sequentially.

---

## Task 1: Descriptive round titles (Item 4 — workflows, TDD)

**Files:**
- Modify: `src/workflows/Services/RunEventFactory.cs`
- Test: `tests/TectikaAgents.Tests/RoundTitleTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/RoundTitleTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~RoundTitle"
```
Expected: FAIL to compile — `RoundTitle` does not exist.

- [ ] **Step 3: Add the `RoundTitle` helper and use it**

In `src/workflows/Services/RunEventFactory.cs`, add this `using` if not present at the top (it already has `using TectikaAgents.Core.Models;`):

```csharp
using System.Linq;
```

Add this class at the **bottom of the file** (after the closing brace of `RunEventFactory`):

```csharp
/// <summary>Produces a human-readable title for a round's RoundStarted event. Prefers the agent's
/// own round_intent; otherwise synthesizes one from the round's real activity so the Activity tab
/// never shows a bare "Round n".</summary>
public static class RoundTitle
{
    public static string Synthesize(RoundOutcome outcome, int round)
    {
        if (!string.IsNullOrWhiteSpace(outcome.RoundIntent))
            return outcome.RoundIntent!.Trim();

        if (outcome.Kind == RoundKind.Final && !string.IsNullOrWhiteSpace(outcome.FinalText))
            return Truncate(outcome.FinalText!.Trim(), 70);

        var verbs = outcome.ToolCalls
            .Select(tc => tc.Name)
            .Where(n => n is not "round_intent" and not "update_brief")
            .Distinct()
            .Take(3)
            .Select(FriendlyVerb)
            .ToList();
        if (verbs.Count > 0)
            return Capitalize(string.Join(", ", verbs));

        return $"Round {round + 1}";
    }

    private static string FriendlyVerb(string tool) => tool switch
    {
        "get_board_overview" => "read board",
        "search_tasks" => "searched board",
        "get_task" => "read task",
        "get_artifact" => "read artifact",
        _ when tool.Contains("github") || tool.Contains("branch") || tool.Contains("pull_request")
            || tool.Contains("push") || tool.Contains("commit") => "used GitHub",
        _ => tool,
    };

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
```

Then, in `RunEventFactory.BuildRoundEvents`, change the parent title line (currently):
```csharp
            Title = string.IsNullOrWhiteSpace(outcome.RoundIntent) ? $"Round {round + 1}" : outcome.RoundIntent,
```
to:
```csharp
            Title = RoundTitle.Synthesize(outcome, round),
```

- [ ] **Step 4: Run the test to verify it passes**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~RoundTitle"
```
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full suite (RunEventFactory has existing tests)**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj
```
Expected: all pass. (If an existing `RunEventFactoryTests` asserted the literal `"Round 1"` title for a no-intent/no-tools case, it still holds; a case with tools/final text now yields a descriptive title — update that assertion only if it breaks, to match the synthesized value.)

- [ ] **Step 6: Commit**

```bash
git add src/workflows/Services/RunEventFactory.cs tests/TectikaAgents.Tests/RoundTitleTests.cs
git commit -m "feat(workflows): synthesize descriptive round titles"
```

---

## Task 2: Real-time user messages over SSE (Item 2a — API)

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/ChatService.cs`

- [ ] **Step 1: Inject `SseConnectionManager`**

In `ChatService.cs`, add a field next to the existing ones (after `private readonly ILogger<ChatService> _logger;`):
```csharp
    private readonly SseConnectionManager _sse;
```
Change the constructor signature + body to accept and assign it:
```csharp
    public ChatService(ICosmosDbService cosmos, IHttpClientFactory httpFactory,
        IOptions<DurableFunctionsSettings> settings, SseConnectionManager sse, ILogger<ChatService> logger)
    {
        _cosmos = cosmos;
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _sse = sse;
        _logger = logger;
    }
```

- [ ] **Step 2: Broadcast the echoed user message**

Replace the `EchoUserMessageAsync` method (currently an expression-bodied method returning the create Task):
```csharp
    private Task EchoUserMessageAsync(string runId, string taskId, int round, string text, CancellationToken ct) =>
        _cosmos.CreateRunEventAsync(new RunEvent
        {
            RunId = runId, TaskId = taskId, Round = round, Kind = RunEventKind.UserMessage, Title = text
        }, ct);
```
with:
```csharp
    private async Task EchoUserMessageAsync(string runId, string taskId, int round, string text, CancellationToken ct)
    {
        var ev = await _cosmos.CreateRunEventAsync(new RunEvent
        {
            RunId = runId, TaskId = taskId, Round = round, Kind = RunEventKind.UserMessage, Title = text
        }, ct);
        // Push to everyone watching this run's SSE stream (same API instance) so other participants see
        // the message in real time; the client's 4s poll is the cross-instance / missed-event backstop.
        await _sse.BroadcastAsync(AgentEvent.FromRunEvent(ev), ct);
    }
```

`SseConnectionManager` and `AgentEvent` are already in scope (`SseConnectionManager` is in `TectikaAgents.Api.Services`, the same namespace as `ChatService`; `AgentEvent` / `RunEvent` are in `TectikaAgents.Core.Models`, already imported). `CreateRunEventAsync` returns the saved `RunEvent`.

- [ ] **Step 3: Build the API**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj
```
Expected: Build succeeded. (`SseConnectionManager` is already registered in DI — it's consumed by `ServiceBusListenerService` and the SSE controller — so constructor injection resolves.)

- [ ] **Step 4: Run the test suite (no regressions)**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj
```
Expected: all pass. (If `ChatServiceClearTests` constructs `ChatService` directly, it must now pass an `SseConnectionManager` — construct one with `new SseConnectionManager(NullLogger<SseConnectionManager>.Instance)`. Update that test's construction only if it fails to compile.)

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/ChatService.cs tests/TectikaAgents.Tests
git commit -m "feat(api): broadcast user chat messages over SSE in real time"
```

---

## Task 3: Remove the Updates tab (Item 1 — web)

**Files:**
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`
- Modify: `src/web/tectika-board/src/lib/board-context.tsx`

- [ ] **Step 1: Remove the tab from `ItemPanel.tsx`**

Change the `Tab` type (line ~22):
```tsx
type Tab = 'chat' | 'updates' | 'activity' | 'details' | 'bridge';
```
to:
```tsx
type Tab = 'chat' | 'activity' | 'details' | 'bridge';
```

Change the tab-button list (the array literal, line ~81):
```tsx
            {(['chat', 'updates', 'activity', 'details', 'bridge'] as Tab[]).map(t => (
```
to:
```tsx
            {(['chat', 'activity', 'details', 'bridge'] as Tab[]).map(t => (
```

Remove the Updates render line (line ~87):
```tsx
            {tab === 'updates' && <UpdatesTab task={task} />}
```

- [ ] **Step 2: Delete the `UpdatesTab` component and `renderMentions` helper**

Delete the entire `UpdatesTab` function (the block starting `// ── Updates (comments) ───` / `function UpdatesTab({ task }: { task: AgentTask }) {` through its closing `}`) and the `renderMentions` function immediately after it (`function renderMentions(body: string) { ... }`). These are used nowhere else.

- [ ] **Step 3: Remove the now-dead comment plumbing from `board-context.tsx`**

`comments` and `addComment` were consumed only by `UpdatesTab`. Remove them from the context:

- In the `BoardContextValue` interface, delete these two lines:
```tsx
  comments: Comment[];
```
```tsx
  addComment: (taskId: string, body: string) => void;
```
- Delete the `addComment` callback definition:
```tsx
  const addComment = useCallback((taskId: string, body: string) => {
    const mentions = Array.from(body.matchAll(/@([\w.@-]+)/g)).map(m => m[1]);
    const c: Comment = { id: uid('c'), taskId, authorId: CURRENT_USER.id, body, mentions, createdAt: new Date().toISOString() };
    setCfg(prev => ({ ...prev, comments: [...prev.comments, c] }));
  }, []);
```
- In the `value` object near the bottom, change:
```tsx
    comments: cfg.comments, activity: cfg.activity, addComment, logActivity,
```
to:
```tsx
    activity: cfg.activity, logActivity,
```

Leave `BoardConfig.comments`, `defaultConfig`, and `seedCollaboration` as-is (the stored data is harmless and `Comment` is still referenced by `BoardConfig`). Do not touch `activity`/`logActivity`.

- [ ] **Step 4: Typecheck + lint**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment/src/web/tectika-board && npx tsc --noEmit && npx eslint src/components/workspace/ItemPanel.tsx src/lib/board-context.tsx
```
Expected: tsc clean (exit 0). eslint: no NEW issues vs. baseline (the pre-existing `AgentConfigEditor` `react-hooks/refs` errors + the `[task.id]` exhaustive-deps warning may remain). If tsc flags `Comment` as an unused import in `board-context.tsx`, that means it's no longer referenced — remove `Comment` from the `./types` import to satisfy it; otherwise leave the import.

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx src/web/tectika-board/src/lib/board-context.tsx
git commit -m "feat(web): remove redundant Updates tab"
```

---

## Task 4: Auto-refresh the chat (Item 2b — web)

**Files:**
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`

- [ ] **Step 1: Add a `mergeById` helper**

Immediately above the `useRunEvents` function (`function useRunEvents(task: AgentTask, activeRunId?: string): RunEvent[] {`), add:

```tsx
// Union two event lists by id (incoming wins on collision). Returns the previous array unchanged when
// `incoming` introduces no new ids, so a poll that finds nothing new triggers no re-render.
function mergeById(prev: RunEvent[], incoming: RunEvent[]): RunEvent[] {
  if (incoming.length === 0) return prev;
  const map = new Map(prev.map(e => [e.id, e]));
  let hasNew = false;
  for (const e of incoming) { if (!map.has(e.id)) hasNew = true; map.set(e.id, e); }
  if (!hasNew && map.size === prev.length) return prev;
  return Array.from(map.values());
}
```

- [ ] **Step 2: Poll inside `useRunEvents`**

Replace the first effect of `useRunEvents` (the mount/fetch effect):
```tsx
  useEffect(() => {
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- clear stale trace when switching tasks
    setEvents([]);
    api.tasks.events(task.boardId, task.id).then(list => { if (alive) setEvents(list); }).catch(() => {});
    return () => { alive = false; };
  }, [task.boardId, task.id]);
```
with:
```tsx
  useEffect(() => {
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- clear stale trace when switching tasks
    setEvents([]);
    const load = () => api.tasks.events(task.boardId, task.id)
      .then(list => { if (alive) setEvents(prev => mergeById(prev, list)); })
      .catch(() => {});
    load();
    // Poll as a backstop so messages from other users (which aren't pushed when this client missed the
    // SSE frame, or has no active-run subscription yet) still appear within ~4s. Paused when hidden.
    const poll = setInterval(() => {
      if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
      load();
    }, 4000);
    return () => { alive = false; clearInterval(poll); };
  }, [task.boardId, task.id]);
```

The live SSE effect below it is unchanged; with the API now broadcasting user messages (Task 2), they arrive over SSE in real time, and this poll is the backstop. Both paths dedupe by id.

- [ ] **Step 3: Typecheck + lint**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment/src/web/tectika-board && npx tsc --noEmit && npx eslint src/components/workspace/ItemPanel.tsx
```
Expected: tsc clean; no new eslint issues.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): auto-refresh chat events (poll backstop)"
```

---

## Task 5: Collapsible activity rows (Item 3 — web)

**Files:**
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`

- [ ] **Step 1: Make `HistoryStep` expandable**

Replace the `HistoryStep` function (the tool/artifact branch — keep the `round_intent` early return identical) with:

```tsx
// One real step in the history layer: round intent → subtle header; tool/artifact → ✓ line.
// Tool/artifact rows are click-to-expand: collapsed truncates to one line, expanded shows the full
// untruncated tool/args/result. A step still in flight (no result yet) shows a spinner.
function HistoryStep({ ev }: { ev: RunEvent }) {
  const [open, setOpen] = useState(false);

  if (ev.toolName === 'round_intent') {
    const intent = ev.toolArgsSummary || ev.title;
    if (!intent) return null;
    return (
      <div className="flex items-center gap-2 ml-1 mt-1 text-[11.5px] text-[var(--muted)]">
        <span className="text-[var(--primary)]">▸</span><span className="italic truncate">{intent}</span>
      </div>
    );
  }

  const running = !ev.resultSummary && ev.kind !== 'ArtifactWritten';
  const { verb, obj, res } = stepLabel(ev);
  const expandable = !!(obj || res);

  return (
    <button type="button" disabled={!expandable} onClick={() => setOpen(o => !o)}
      className="w-full flex items-start gap-2 ml-1 text-[12px] text-[var(--foreground)] text-left">
      {running
        ? <span className="w-3.5 h-3.5 mt-0.5 rounded-full border-2 border-[var(--border)] border-t-[var(--primary)] animate-spin shrink-0" />
        : <span className="w-4 h-4 mt-0.5 rounded-full bg-[#00c875] text-white grid place-items-center text-[9px] shrink-0">✓</span>}
      <span className={`flex-1 min-w-0 ${open ? 'whitespace-pre-wrap break-words' : 'truncate'}`}>
        <span className="text-[var(--muted)]">{verb}</span>
        {obj && <span className="font-mono text-[var(--primary)]"> {obj}</span>}
        {res && <span className="text-[var(--muted-2)]"> · {res}</span>}
      </span>
      {expandable && <Icon.chevronDown size={12} className={`mt-1 text-[var(--muted-2)] shrink-0 transition-transform ${open ? 'rotate-180' : ''}`} />}
    </button>
  );
}
```

`useState` and `Icon` are already imported in this file; `Icon.chevronDown` is already used elsewhere here.

- [ ] **Step 2: Typecheck + lint**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment/src/web/tectika-board && npx tsc --noEmit && npx eslint src/components/workspace/ItemPanel.tsx
```
Expected: tsc clean; no new eslint issues.

- [ ] **Step 3: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): collapsible activity rows in chat"
```

---

## Task 6: Full build + smoke verification

**Files:** none (verification only)

- [ ] **Step 1: Backend — full test suite**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj
```
Expected: all pass (incl. the 4 new `RoundTitle` tests).

- [ ] **Step 2: Web — production build**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment/src/web/tectika-board && npm run build -- --webpack
```
Expected: build succeeds.

- [ ] **Step 3: Manual smoke (deployed — web + api + workflows)**

1. Open a task panel → only **Chat · Activity · Details · CLI Bridge** tabs (no Updates).
2. Two browsers on the same task's Chat → send from one → it appears in the other in ~1s (SSE push), and within ~4s even if the SSE frame was missed (poll).
3. In the chat history, a long activity row truncates on one line and **expands inline** on click (chevron), showing the full tool/args/result.
4. In the **Activity** tab, round rows show descriptive titles (e.g. "Read board, searched board", or an answer snippet) instead of "Round n".

- [ ] **Step 4: Final commit (only if the smoke required a fix)**

```bash
git add -A && git commit -m "fix(chat-round2): smoke fixes"
```

---

## Notes for the implementer

- **Deploy footprint:** web + api + workflows. The user merges/deploys manually.
- **Item 2 is two layers:** SSE push (API, Task 2) + poll backstop (web, Task 4). Direct SSE broadcast only reaches clients on the same API instance; the poll bounds cross-instance staleness to ~4s.
- **Item 4 only affects the Activity tab's `RoundStarted` rows.** The in-chat history derives its header from the `round_intent` ToolCall, not the parent title, so it's unchanged.
- **Don't restructure `board-context.tsx`** beyond removing the dead `comments`/`addComment` — it's large and central.
```
