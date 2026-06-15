# Chat Slash-Commands Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Add a `/`-triggered, fuzzy-searchable command palette to the per-task chat, with commands `/clear`, `/compact`, `/stop`, `/retry`, `/help`.

**Architecture:** A declarative frontend command **registry** drives an inline `CommandMenu` palette over the chat input; each command runs client-side (`/help`, `/retry`) or calls a thin dedicated endpoint (`/clear`, `/stop`, `/compact`). `/clear` is a non-destructive **boundary** (`AgentTask.ChatClearedAt`) + thread reset + brief clear; the chat/activity views render only events after the boundary.

**Tech Stack:** C# / .NET 10 (xUnit), Azure Functions Durable (.NET isolated), Next.js 16 / React (`'use client'`), Cosmos.

**Spec:** `docs/superpowers/specs/2026-06-15-chat-slash-commands-design.md`

**Conventions:**
- Core models: `src/core/TectikaAgents.Core/Models/`, namespace `TectikaAgents.Core.Models`, `[JsonPropertyName("camelCase")]` on every prop.
- xUnit tests: `tests/TectikaAgents.Tests/`, no namespace, `using Xunit;`. Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~<Class>"`.
- **Frontend has no JS test runner** (scripts: dev/build/start/lint). Frontend tasks verify with `npx tsc --noEmit -p tsconfig.json` (clean) + `npx eslint <file>` (no new errors). The web build uses Webpack (Dockerfile `next build --webpack`); locally `npm run build -- --webpack` for a full check.
- Mirror the existing global palette `src/web/tectika-board/src/components/command/CommandPalette.tsx` (fuzzy filter via `fuzzyScore(text, query)`, `↑/↓/Enter` nav, `active` index).

---

## File Structure

**Backend**
- Modify `src/core/.../Models/AgentTask.cs` — add `ChatClearedAt`.
- Modify `src/api/.../Services/ChatService.cs` (+ `IChatService`) — add `ClearAsync`, `StopAsync` (reuses its Cosmos + workflow-HTTP plumbing).
- Modify `src/api/.../Controllers/TasksController.cs` — add `POST {taskId}/clear`, `POST {taskId}/stop`.
- Modify `src/workflows/Triggers/HttpTrigger.cs` — add `Terminate` trigger (`pipelines/{instanceId}/terminate`).
- *(Slice 2 — /compact)* Modify `src/workflows/Triggers/HttpTrigger.cs` + an activity for summarization, and `ChatService.CompactAsync` + controller route.

**Frontend**
- Modify `src/web/.../lib/types.ts` — add `chatClearedAt` to `AgentTask`.
- Modify `src/web/.../lib/api.ts` — add `tasks.clear`, `tasks.stop`, `tasks.compact`.
- Create `src/web/.../lib/chat-commands.ts` — `ChatCommand` type + `chatCommands` registry.
- Create `src/web/.../components/workspace/CommandMenu.tsx` — the inline palette.
- Modify `src/web/.../components/workspace/ItemPanel.tsx` (`ChatTab` + `useRunEvents`) — `/`-trigger + render `CommandMenu` + filter events by `chatClearedAt`.

---

## Slice 1 — Core (clear / stop / retry / help + palette)

### Task 1: `AgentTask.ChatClearedAt`

**Files:** Modify `src/core/TectikaAgents.Core/Models/AgentTask.cs`; Test `tests/TectikaAgents.Tests/AgentTaskChatClearedAtTests.cs`.

- [ ] **Step 1: Failing test**
```csharp
using System.Text.Json;
using TectikaAgents.Core.Models;
using Xunit;

public class AgentTaskChatClearedAtTests
{
    [Fact]
    public void ChatClearedAt_DefaultsNull_AndRoundTrips()
    {
        Assert.Null(new AgentTask().ChatClearedAt);
        var t = new AgentTask { Id = "t", ChatClearedAt = DateTimeOffset.Parse("2026-06-15T10:00:00Z") };
        var json = JsonSerializer.Serialize(t);
        Assert.Contains("\"chatClearedAt\":", json);
        Assert.Equal(t.ChatClearedAt, JsonSerializer.Deserialize<AgentTask>(json)!.ChatClearedAt);
    }
}
```
- [ ] **Step 2: Run → FAIL** (`AgentTask` has no `ChatClearedAt`).
Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~AgentTaskChatClearedAtTests"`
- [ ] **Step 3: Add the field** — in `AgentTask.cs`, after the `prompt` property:
```csharp
    [JsonPropertyName("chatClearedAt")]
    public DateTimeOffset? ChatClearedAt { get; set; }
```
- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit**
```bash
git add src/core/TectikaAgents.Core/Models/AgentTask.cs tests/TectikaAgents.Tests/AgentTaskChatClearedAtTests.cs
git commit -m "feat(core): add AgentTask.ChatClearedAt (chat clear boundary)"
```

---

### Task 2: `/clear` — service + endpoint

`/clear` patches the task: `FoundryThreadId=null`, `TaskBrief=""`, `ChatClearedAt=now`. Implemented in `ChatService` (it already has `ICosmosDbService`).

**Files:** Modify `src/api/TectikaAgents.Api/Services/ChatService.cs` (+ `IChatService`); Modify `src/api/TectikaAgents.Api/Controllers/TasksController.cs`; Test `tests/TectikaAgents.Tests/ChatServiceClearTests.cs`.

- [ ] **Step 1: Failing test** (uses the in-memory cosmos already used by the API for tests)
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Models;
using Xunit;

public class ChatServiceClearTests
{
    private static ChatService Make(InMemoryCosmosDbService cosmos) =>
        new(cosmos, new System.Net.Http.HttpClientFactoryStub(),
            Options.Create(new DurableFunctionsSettings()), NullLogger<ChatService>.Instance);

    [Fact]
    public async Task ClearAsync_ResetsThreadBriefAndSetsBoundary()
    {
        var cosmos = new InMemoryCosmosDbService();
        var task = await cosmos.CreateTaskAsync(new AgentTask {
            Id = "t1", BoardId = "b1", TenantId = "default", Title = "T",
            FoundryThreadId = "conv_x", TaskBrief = "old notes" });

        var ok = await Make(cosmos).ClearAsync("b1", "t1");

        Assert.True(ok);
        var after = await cosmos.GetTaskAsync("b1", "t1");
        Assert.Null(after!.FoundryThreadId);
        Assert.Equal("", after.TaskBrief);
        Assert.NotNull(after.ChatClearedAt);
    }
}
```
> If `InMemoryCosmosDbService` lacks `CreateTaskAsync`/`GetTaskAsync`, use the methods it does expose (read `src/api/.../Services/InMemoryCosmosDbService.cs` first). `HttpClientFactoryStub` may not exist — if so, pass a real `IHttpClientFactory` via `new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>()`; ClearAsync makes no HTTP call so the factory is unused here.

- [ ] **Step 2: Run → FAIL** (`ClearAsync` missing).
- [ ] **Step 3: Add to `IChatService`** (in ChatService.cs):
```csharp
    /// <summary>Reset the agent's context: new conversation next run, cleared brief, and a transcript
    /// boundary (ChatClearedAt). Non-destructive — RunEvents are kept, just hidden by the UI.</summary>
    Task<bool> ClearAsync(string boardId, string taskId, CancellationToken ct = default);
```
- [ ] **Step 4: Implement in `ChatService`:**
```csharp
    public async Task<bool> ClearAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        if (task is null) return false;
        task.FoundryThreadId = null;
        task.TaskBrief = "";
        task.ChatClearedAt = DateTimeOffset.UtcNow;
        await _cosmos.UpdateTaskAsync(task, ct);
        _logger.LogInformation("[Chat] cleared task {TaskId}", taskId);
        return true;
    }
```
- [ ] **Step 5: Add the controller endpoint** (TasksController.cs, after `Chat`):
```csharp
    /// <summary>/clear — reset the agent's context (new conversation, cleared brief + transcript boundary).</summary>
    [HttpPost("{taskId}/clear")]
    public async Task<IActionResult> Clear(string boardId, string taskId, CancellationToken ct) =>
        await _chat.ClearAsync(boardId, taskId, ct) ? Ok() : NotFound("Task not found.");
```
- [ ] **Step 6: Run → PASS;** `dotnet build TectikaAgents.slnx` → 0 errors.
- [ ] **Step 7: Commit**
```bash
git add src/api/TectikaAgents.Api/Services/ChatService.cs src/api/TectikaAgents.Api/Controllers/TasksController.cs tests/TectikaAgents.Tests/ChatServiceClearTests.cs
git commit -m "feat(api): /clear endpoint — reset thread, brief, and set chat boundary"
```

---

### Task 3: `/stop` — workflows terminate trigger + endpoint

**Files:** Modify `src/workflows/Triggers/HttpTrigger.cs`; Modify `src/api/.../Services/ChatService.cs` (+ interface); Modify `src/api/.../Controllers/TasksController.cs`.

- [ ] **Step 1: Add the workflows `Terminate` trigger** (HttpTrigger.cs, mirror `RaiseUserMessage`):
```csharp
    [Function(nameof(Terminate))]
    public async Task<HttpResponseData> Terminate(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "pipelines/{instanceId}/terminate")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient durableClient,
        FunctionContext context)
    {
        await durableClient.TerminateInstanceAsync(instanceId, "user /stop");
        _logger.LogInformation("[HttpTrigger] terminated instance {InstanceId}", instanceId);
        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteStringAsync("terminated");
        return response;
    }
```
- [ ] **Step 2: Add `StopAsync` to `IChatService` + implement** (ChatService.cs) — reuses `BuildUrl`/`PostAsync` + `GetRunAsync`:
```csharp
    // interface:
    Task<bool> StopAsync(string boardId, string taskId, CancellationToken ct = default);

    // implementation:
    public async Task<bool> StopAsync(string boardId, string taskId, CancellationToken ct = default)
    {
        var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
        var run = task?.WorkflowRunId is null ? null : await _cosmos.GetRunAsync(taskId, task.WorkflowRunId, ct);
        if (run?.DurableFunctionInstanceId is null) return false;          // nothing running
        await PostAsync(BuildUrl($"{run.DurableFunctionInstanceId}/terminate"), new { }, ct);
        run.Status = RunStatus.Cancelled;
        await _cosmos.UpdateRunAsync(run, ct);
        _logger.LogInformation("[Chat] stopped run {RunId} task {TaskId}", run.Id, taskId);
        return true;
    }
```
> Confirm `ICosmosDbService` has `GetRunAsync(taskId, runId, ct)` and `UpdateRunAsync(run, ct)` (the API side). If the API's cosmos interface names differ, read `ICosmosDbService.cs` and use the actual run read/update methods.

- [ ] **Step 3: Add the controller endpoint:**
```csharp
    /// <summary>/stop — terminate the task's active run.</summary>
    [HttpPost("{taskId}/stop")]
    public async Task<IActionResult> Stop(string boardId, string taskId, CancellationToken ct) =>
        await _chat.StopAsync(boardId, taskId, ct) ? Ok() : Ok(new { stopped = false }); // no active run → benign
```
- [ ] **Step 4: Build** → `dotnet build TectikaAgents.slnx` → 0 errors. (No unit test: termination is an HTTP+Durable side-effect; covered by the live retest.)
- [ ] **Step 5: Commit**
```bash
git add src/workflows/Triggers/HttpTrigger.cs src/api/TectikaAgents.Api/Services/ChatService.cs src/api/TectikaAgents.Api/Controllers/TasksController.cs
git commit -m "feat: /stop endpoint + workflows terminate trigger"
```

---

### Task 4: Frontend types + api client

**Files:** Modify `src/web/.../lib/types.ts`, `src/web/.../lib/api.ts`.

- [ ] **Step 1: Add `chatClearedAt`** to the `AgentTask` interface (types.ts), after `prompt?: string;`:
```ts
  chatClearedAt?: string;
```
- [ ] **Step 2: Add command methods** to the `tasks` group in `api.ts` (after `events`):
```ts
    clear: (boardId: string, taskId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/tasks/${taskId}/clear`, { method: 'POST' }),
    stop: (boardId: string, taskId: string) =>
      fetchApi<void>(`/api/boards/${boardId}/tasks/${taskId}/stop`, { method: 'POST' }),
    compact: (boardId: string, taskId: string) =>
      fetchApi<{ summarized: boolean }>(`/api/boards/${boardId}/tasks/${taskId}/compact`, { method: 'POST' }),
```
- [ ] **Step 3: Verify** — `cd src/web/tectika-board && npx tsc --noEmit -p tsconfig.json` → clean.
- [ ] **Step 4: Commit**
```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/lib/api.ts
git commit -m "feat(web): task.chatClearedAt + api.tasks.clear/stop/compact"
```

---

### Task 5: Command registry (`chat-commands.ts`)

**Files:** Create `src/web/tectika-board/src/lib/chat-commands.ts`.

- [ ] **Step 1: Create the registry** (pure data + `run`/`enabled` over a context):
```ts
import { api } from './api';
import type { IconName } from '@/components/ui/icons';

/** Everything a command needs from the chat to do its job. */
export interface ChatCommandContext {
  boardId: string;
  taskId: string;
  isRunning: boolean;          // a run is active
  lastUserText?: string;       // last human message (for /retry)
  refreshTask: () => void;     // re-fetch the task (pick up chatClearedAt / thread reset)
  resend: (text: string) => void;  // re-send a chat message
  openHelp: () => void;        // show the /help list
  toast: (msg: string, kind?: 'success' | 'error') => void;
}

export interface ChatCommand {
  name: string;                // without the slash, e.g. "clear"
  description: string;
  icon: IconName;
  enabled: (ctx: ChatCommandContext) => boolean;
  run: (ctx: ChatCommandContext) => void | Promise<void>;
}

export const chatCommands: ChatCommand[] = [
  {
    name: 'clear', description: 'Clear the conversation — fresh context for the agent',
    icon: 'refresh', enabled: () => true,
    run: async (c) => {
      try { await api.tasks.clear(c.boardId, c.taskId); c.refreshTask(); c.toast('Conversation cleared'); }
      catch { c.toast('Could not clear the conversation', 'error'); }
    },
  },
  {
    name: 'compact', description: 'Summarize, then start fresh — keeps the gist, drops the bulk',
    icon: 'edit', enabled: () => true,
    run: async (c) => {
      try {
        const r = await api.tasks.compact(c.boardId, c.taskId); c.refreshTask();
        c.toast(r.summarized ? 'Conversation compacted' : 'Couldn’t summarize — cleared instead');
      } catch { c.toast('Could not compact the conversation', 'error'); }
    },
  },
  {
    name: 'stop', description: 'Stop the agent (cancel the current run)',
    icon: 'x', enabled: (c) => c.isRunning,
    run: async (c) => {
      try { await api.tasks.stop(c.boardId, c.taskId); c.refreshTask(); c.toast('Run stopped'); }
      catch { c.toast('Could not stop the run', 'error'); }
    },
  },
  {
    name: 'retry', description: 'Re-send your last message',
    icon: 'refresh', enabled: (c) => !c.isRunning && !!c.lastUserText,
    run: (c) => { if (c.lastUserText) c.resend(c.lastUserText); },
  },
  {
    name: 'help', description: 'Show available commands',
    icon: 'robot', enabled: () => true,
    run: (c) => c.openHelp(),
  },
];
```
> Confirm icon names exist in `src/components/ui/icons.tsx` (`refresh`, `edit`, `x`, `robot` were verified present this session; if any is missing, pick a present one).

- [ ] **Step 2: Verify** — `npx tsc --noEmit -p tsconfig.json` → clean; `npx eslint src/lib/chat-commands.ts` → no errors.
- [ ] **Step 3: Commit**
```bash
git add src/web/tectika-board/src/lib/chat-commands.ts
git commit -m "feat(web): chat command registry (/clear /compact /stop /retry /help)"
```

---

### Task 6: `CommandMenu` palette component

Inline floating list above the chat input — mirrors `CommandPalette` (fuzzy filter + `↑/↓/Enter`), but anchored, not full-screen.

**Files:** Create `src/web/tectika-board/src/components/workspace/CommandMenu.tsx`.

- [ ] **Step 1: Create the component:**
```tsx
'use client';
import React, { useEffect, useMemo, useState } from 'react';
import { fuzzyScore } from '@/lib/format';
import { Icon } from '@/components/ui/icons';
import { chatCommands, type ChatCommand, type ChatCommandContext } from '@/lib/chat-commands';

/** The query is the text typed after the leading "/". onRun runs a command; onClose dismisses. */
export function CommandMenu({ query, ctx, onClose }: {
  query: string; ctx: ChatCommandContext; onClose: () => void;
}) {
  const [active, setActive] = useState(0);

  const filtered = useMemo<ChatCommand[]>(() => {
    if (!query) return chatCommands;
    return chatCommands
      .map(c => ({ c, s: Math.max(fuzzyScore(c.name, query), fuzzyScore(c.description, query)) }))
      .filter(x => x.s > 0).sort((a, b) => b.s - a.s).map(x => x.c);
  }, [query]);

  // eslint-disable-next-line react-hooks/set-state-in-effect -- reset highlight when the filter changes
  useEffect(() => { setActive(0); }, [query]);

  // Keyboard handled by the parent textarea via handleKey (exported below) to share the same input.
  CommandMenu.activeRef = { active, setActive, filtered, ctx, onClose };

  if (filtered.length === 0)
    return <Shell><div className="px-3 py-3 text-xs text-[var(--muted)]">No matching commands</div></Shell>;

  return (
    <Shell>
      {filtered.map((c, i) => {
        const ok = c.enabled(ctx);
        const I = Icon[c.icon];
        return (
          <button key={c.name} disabled={!ok}
            onMouseEnter={() => setActive(i)}
            onClick={() => { if (ok) { c.run(ctx); onClose(); } }}
            className={`w-full flex items-center gap-2.5 px-3 py-2 text-left ${i === active ? 'bg-[var(--primary-light)]' : ''} ${ok ? '' : 'opacity-40 cursor-not-allowed'}`}>
            <I size={14} className="text-[var(--muted)] shrink-0" />
            <span className="text-[13px] text-[var(--foreground)] font-medium">/{c.name}</span>
            <span className="text-[11px] text-[var(--muted)] truncate">{c.description}</span>
          </button>
        );
      })}
    </Shell>
  );
}

function Shell({ children }: { children: React.ReactNode }) {
  return (
    <div className="absolute bottom-full left-0 right-0 mb-1 z-20 rounded-lg border border-[var(--border)] bg-[var(--background)] shadow-xl overflow-hidden max-h-[40vh] overflow-y-auto">
      {children}
    </div>
  );
}
```
> The parent owns the textarea + keyboard. To keep `↑/↓/Enter` driving the menu, expose a static handler the parent calls (Step 2). The `CommandMenu.activeRef` line above is a lightweight bridge; if the reviewer prefers, lift `active`/`filtered` into the parent and pass them down — but keep keyboard in the parent so the textarea stays focused.

- [ ] **Step 2: Add a keyboard helper export** at the bottom of the file (used by `ChatTab`):
```tsx
// Lift the filter so the parent can drive selection from the textarea's onKeyDown.
export function filterCommands(query: string) {
  if (!query) return chatCommands;
  return chatCommands
    .map(c => ({ c, s: Math.max(fuzzyScore(c.name, query), fuzzyScore(c.description, query)) }))
    .filter(x => x.s > 0).sort((a, b) => b.s - a.s).map(x => x.c);
}
```
> **Refactor note for the implementer:** the cleanest final shape is *parent-owns-state*: `ChatTab` holds `cmdActive`, computes `filterCommands(query)`, handles `↑/↓/Enter/Esc` in the textarea `onKeyDown`, and `CommandMenu` becomes a pure render of `{ items, active, onHover, onPick, ctx }`. Implement it that way (drop the `activeRef` hack); the code above shows the rendering + filtering pieces to assemble.

- [ ] **Step 3: Verify** — `npx tsc --noEmit` + `npx eslint src/components/workspace/CommandMenu.tsx` → no new errors.
- [ ] **Step 4: Commit**
```bash
git add src/web/tectika-board/src/components/workspace/CommandMenu.tsx
git commit -m "feat(web): inline CommandMenu palette for chat slash-commands"
```

---

### Task 7: Wire the palette into `ChatTab`

**Files:** Modify `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`.

- [ ] **Step 1: Filter events by the boundary** — in `useRunEvents`, after loading/streaming, the events returned should exclude pre-boundary ones. Add a `clearedAt?: string` param and filter:
```tsx
function useRunEvents(task: AgentTask, activeRunId?: string): RunEvent[] {
  // ... existing load + SSE effects unchanged ...
  return useMemo(
    () => (task.chatClearedAt ? events.filter(e => e.timestamp > task.chatClearedAt!) : events),
    [events, task.chatClearedAt],
  );
}
```
(Keep the internal `events` state; wrap the return in the `useMemo` filter. Import `useMemo` is already present.)

- [ ] **Step 2: Add command-palette state + ctx in `ChatTab`.** After the existing `const events = useRunEvents(task, activeRunId);` and state:
```tsx
  const { refreshTask } = useBoard();   // see Step 4 — add to board-context if absent
  const slash = draft.startsWith('/');
  const cmdQuery = slash ? draft.slice(1) : '';
  const [cmdActive, setCmdActive] = useState(0);
  const cmdItems = useMemo(() => slash ? filterCommands(cmdQuery) : [], [slash, cmdQuery]);
  // eslint-disable-next-line react-hooks/set-state-in-effect -- reset highlight as the command query changes
  useEffect(() => { setCmdActive(0); }, [cmdQuery]);

  const lastUserText = useMemo(
    () => [...bubbles].reverse().find(b => b.author === 'human')?.text, [bubbles]);

  const cmdCtx: ChatCommandContext = {
    boardId: task.boardId, taskId: task.id, isRunning: thinking, lastUserText,
    refreshTask: () => refreshTask(task.id),
    resend: (text) => { setDraft(''); api.tasks.chat(task.boardId, task.id, text).then(r => setActiveRunId(r.runId)).catch(() => toast('Could not resend', 'error')); },
    openHelp: () => setHelpOpen(true),
    toast,
  };
  const [helpOpen, setHelpOpen] = useState(false);
  const runCmd = (c: ChatCommand) => { if (c.enabled(cmdCtx)) { c.run(cmdCtx); setDraft(''); } };
```

- [ ] **Step 2b: Imports** — add to the top of ItemPanel.tsx:
```tsx
import { CommandMenu } from './CommandMenu';
import { filterCommands, type ChatCommand, type ChatCommandContext } from '@/lib/chat-commands';
```

- [ ] **Step 3: Intercept keys + render the menu.** In the textarea `onKeyDown`, before the Ctrl+Enter send, add slash-mode navigation; and render `CommandMenu` inside a `relative` wrapper above the textarea:
```tsx
  // textarea onKeyDown:
  onKeyDown={e => {
    if (slash && cmdItems.length) {
      if (e.key === 'ArrowDown') { e.preventDefault(); setCmdActive(a => Math.min(cmdItems.length - 1, a + 1)); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); setCmdActive(a => Math.max(0, a - 1)); return; }
      if (e.key === 'Enter') { e.preventDefault(); runCmd(cmdItems[cmdActive]); return; }
      if (e.key === 'Escape') { e.preventDefault(); setDraft(''); return; }
    }
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); send(); }
  }}
```
Wrap the input row in `<div className="relative">` and render the menu when in slash mode:
```tsx
  <div className="relative">
    {slash && cmdItems.length > 0 && (
      <CommandMenu items={cmdItems} active={cmdActive} ctx={cmdCtx}
        onHover={setCmdActive} onPick={runCmd} />
    )}
    <textarea ... />
  </div>
```
> Adjust `CommandMenu`'s props to the pure form `{ items, active, ctx, onHover, onPick }` per Task 6's refactor note (drop `query`/`onClose`; it just renders).

- [ ] **Step 4: `refreshTask` in board-context.** If `useBoard()` has no `refreshTask`, add one to `src/web/.../lib/board-context.tsx`: `const refreshTask = useCallback(async (id: string) => { const t = await api.tasks.get(boardId, id); setTasks(prev => prev.map(x => x.id === id ? t : x)); }, [boardId]);` and expose it on the context value + type. (Read board-context.tsx to match its patterns.)

- [ ] **Step 5: `/help` panel.** When `helpOpen`, render a small list of `chatCommands` (name + description) as a dismissible overlay (reuse the `Modal` primitive or a simple panel). Minimal:
```tsx
  {helpOpen && (
    <div className="absolute inset-0 z-30 bg-[var(--background)]/95 p-4 overflow-auto" onClick={() => setHelpOpen(false)}>
      <div className="text-sm font-semibold mb-2 text-[var(--foreground)]">Commands</div>
      {chatCommands.map(c => <div key={c.name} className="py-1 text-[13px]"><span className="font-mono text-[var(--primary)]">/{c.name}</span> <span className="text-[var(--muted)]">{c.description}</span></div>)}
      <div className="text-[11px] text-[var(--muted-2)] mt-3">Click anywhere to close</div>
    </div>
  )}
```
(import `chatCommands` too.)

- [ ] **Step 6: Verify** — `npx tsc --noEmit` clean; `npx eslint src/components/workspace/ItemPanel.tsx` → no NEW errors (pre-existing `AgentConfigEditor` ref errors may remain — leave them); `npm run build -- --webpack` succeeds.
- [ ] **Step 7: Commit**
```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx src/web/tectika-board/src/lib/board-context.tsx
git commit -m "feat(web): / command palette in chat (clear/stop/retry/help) + boundary filtering"
```

---

### Task 8: Build + sweep (Slice 1)

- [ ] **Step 1:** `dotnet build TectikaAgents.slnx` → 0 errors; `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` → all pass.
- [ ] **Step 2:** `cd src/web/tectika-board && npx tsc --noEmit && npm run build -- --webpack` → build succeeds.
- [ ] **Step 3:** `git status -s` → clean (ignore pre-existing `publish*/`).

---

## Slice 2 — `/compact` (LLM summarization)

The only LLM-dependent command. Summarization runs in the **workflows** app (it has `IAgentRuntime` + Foundry), exposed as an HTTP trigger the API calls (like `/stop`).

### Task 9: `Compact` workflow function + activity

**Files:** Modify `src/workflows/Triggers/HttpTrigger.cs` (add `Compact` trigger calling an activity) OR add `src/workflows/Activities/CompactActivity.cs` invoked directly from the trigger.

- [ ] **Step 1:** Add `Compact` HTTP trigger `pipelines/compact/{boardId}/{taskId}` that: loads the task's chat `RunEvent`s since `ChatClearedAt` via `WorkflowCosmosService` (add `GetChatEventsAsync(taskId, since)` if absent), builds a transcript string, calls a **one-shot summarization** through the runtime: reuse the task's role + `IAgentRuntime.RunRoundAsync` with `UserInput = "Summarize the key decisions, state, and open items of this conversation as a short brief:\n\n" + transcript`, a throwaway thread (pass `ThreadId=""` so a fresh conversation is created), and a `NullProjectExplorer` (no tools needed). Take `outcome.FinalText` as the summary.
- [ ] **Step 2:** Patch the task: `TaskBrief = summary`, `FoundryThreadId = null`, `ChatClearedAt = now`. Return `{ summarized: true }`. On any exception → patch the `/clear` reset (no summary) and return `{ summarized: false }`.
- [ ] **Step 3:** Build → 0 errors.
- [ ] **Step 4: Commit** `feat(workflows): /compact summarization trigger`.

> **Live-verify during implementation:** confirm a one-shot `RunRoundAsync` with a fresh thread + `NullProjectExplorer` returns `Final` text for a summarize prompt (the agent shouldn't need tools). If the agent insists on tools, fall back to a direct `/responses` summarization call (model only, no `agent_reference`) — verify that shape against `proj-agentteam` first (see memory `foundry-tool-calling-verified`).

### Task 10: `/compact` API endpoint

**Files:** Modify `src/api/.../Services/ChatService.cs` (+ interface) + `TasksController.cs`.

- [ ] **Step 1:** `CompactAsync(boardId, taskId)` → POST the workflow `pipelines/compact/{boardId}/{taskId}` trigger (reuse `BuildUrl`/`PostAsync`), return the `{ summarized }` it got (parse like `StartAndReadInstanceAsync`).
- [ ] **Step 2:** Controller `POST {taskId}/compact` → `Ok(await _chat.CompactAsync(...))`.
- [ ] **Step 3:** Build → 0 errors. Commit `feat(api): /compact endpoint`.

---

## Self-Review

**1. Spec coverage:** `/clear` (T1-T2, boundary+thread+brief) ✓; `/stop` (T3) ✓; `/retry` (registry T5 + resend in T7) ✓; `/help` (T5+T7) ✓; `/compact` (T9-T10) ✓; palette `/`-trigger + fuzzy + keyboard + disabled states (T5-T7) ✓; `ChatClearedAt` + boundary filter in shared `useRunEvents` (T1, T7) ✓; api/types (T4) ✓. All spec sections mapped.

**2. Placeholder scan:** Backend tasks are code-complete TDD. Frontend tasks show complete component/registry/wiring code; Task 6/7 include an explicit "parent-owns-state" refactor note so the keyboard/state shape is unambiguous (assemble the pure `CommandMenu` from the shown pieces). The two `> confirm …` notes (InMemoryCosmos method names, icon names, board-context `refreshTask`) are verification guards, not deferred work — the code to write is shown. No "TBD"/"handle edge cases".

**3. Type consistency:** `ChatCommand`/`ChatCommandContext` (fields `boardId,taskId,isRunning,lastUserText,refreshTask,resend,openHelp,toast`) defined in T5 and consumed identically in T6/T7. `api.tasks.clear/stop/compact` (T4) match the registry calls (T5). `ChatClearedAt`/`chatClearedAt` consistent across core model (T1), types (T4), filter (T7). `IChatService` gains `ClearAsync`/`StopAsync`/`CompactAsync` used by the controller. ✓
