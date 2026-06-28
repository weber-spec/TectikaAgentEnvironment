# S1 — Git-Always Substrate + Merge-Back Pipeline — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the board workspace always a git repo, and fold a completed run's files into the board main line so downstream tasks can see them — with real merge conflicts surfacing as `NeedsRevision`, never a silent clobber.

**Architecture:** The merge decision lives in the pure, unit-testable loop (`SteerableRunCore`) via a new `IRoundDriver.MergeToMainAsync()` call in the `Final` case: a clean merge → `Completed`; a conflict → `OnMergeConflictAsync` → `NeedsRevision` (main untouched). The real driver maps that onto a new `MergeRunBranchActivity` → `WorkspaceService.MergeRunBranchAsync` → a new executor `POST /worktree/merge` op (commit worktree → merge into the board main-line branch → push if a remote exists → conflict aborts). The executor serializes merges with a process lock (one container per board ⇒ one process). `entrypoint.sh` gains a standalone `git init` so all of this works with no remote.

**Tech Stack:** C# / .NET 10 (Durable Functions, xUnit), Python 3 (the ACI executor `http.server`), Bash (entrypoint). Design spec: `docs/superpowers/specs/2026-06-28-unified-deliverable-model-design.md`.

---

## File structure

| File | Responsibility | Change |
|---|---|---|
| `src/core/TectikaAgents.Core/Models/MergeOutcome.cs` | the merge result DTO shared by loop + service + activity | **create** |
| `src/workflows/Orchestrators/SteerableRunCore.cs` | merge seam in the `Final` case | modify |
| `tests/TectikaAgents.Tests/SteerableRunCoreTests.cs` | loop merge tests + `FakeDriver` impl | modify |
| `src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs` | `MergeRunBranchAsync` contract | modify |
| `src/workflows/Services/WorkspaceService.cs` | merge POST + `ParseMerge` JSON | modify |
| `tests/TectikaAgents.Tests/WorkspaceServiceMergeParseTests.cs` | `ParseMerge` unit tests | **create** |
| `src/workflows/Activities/MergeRunBranchActivity.cs` | Durable activity → service | **create** |
| `src/workflows/Orchestrators/SteerableAgentOrchestrator.cs` | `DurableRoundDriver` merge wiring | modify |
| `docker/workspace-executor/executor.py` | `POST /worktree/merge` op + merge lock | modify |
| `docker/workspace-executor/entrypoint.sh` | standalone `git init -b main` | modify |

Tasks 1–4 are real TDD (pure C#). Tasks 5–6 (Python/Bash) have **no unit harness in this repo** — they are verified by an explicit smoke procedure in Task 7. That asymmetry is called out, not hidden.

---

## Task 1: Merge seam in the pure loop (`MergeOutcome` + `IRoundDriver` + `SteerableRunCore`)

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/MergeOutcome.cs`
- Modify: `src/workflows/Orchestrators/SteerableRunCore.cs`
- Test: `tests/TectikaAgents.Tests/SteerableRunCoreTests.cs`

- [ ] **Step 1: Create the shared DTO**

`src/core/TectikaAgents.Core/Models/MergeOutcome.cs`:
```csharp
namespace TectikaAgents.Core.Models;

/// <summary>Result of folding a completed run's branch into the board main line.
/// Ok = a clean merge (main advanced); otherwise a conflict (main left untouched), with the
/// conflicting file paths for the user-facing message.</summary>
public sealed record MergeOutcome(bool Ok, IReadOnlyList<string> ConflictFiles)
{
    public static MergeOutcome Success() => new(true, System.Array.Empty<string>());
    public static MergeOutcome Conflict(IReadOnlyList<string> files) => new(false, files);
}
```

- [ ] **Step 2: Add the two driver methods to `IRoundDriver`** (in `SteerableRunCore.cs`, after `OnExhaustedAsync`, ~line 25)

```csharp
    /// <summary>Fold this run's branch into the board main line before declaring success. A clean
    /// merge returns Ok; a conflict returns !Ok and the loop routes to NeedsRevision (main untouched).</summary>
    Task<MergeOutcome> MergeToMainAsync();

    /// <summary>A merge conflict blocked completion: mark the run NeedsRevision with a user-facing
    /// message naming the conflicting files, so the task lands in Review for a human or a re-run
    /// (which rebases on the now-advanced main).</summary>
    Task OnMergeConflictAsync(MergeOutcome merge, RoundOutcome? last);
```

- [ ] **Step 3: Write the failing loop tests** — add to `SteerableRunCoreTests.cs`. First extend `FakeDriver` (after line 28) with the two new members:

```csharp
        public MergeOutcome MergeResult = MergeOutcome.Success();   // override per-test
        public int MergeCalls;
        public MergeOutcome? ConflictReported;
        public Task<MergeOutcome> MergeToMainAsync() { MergeCalls++; return Task.FromResult(MergeResult); }
        public Task OnMergeConflictAsync(MergeOutcome merge, RoundOutcome? last) { ConflictReported = merge; return Task.CompletedTask; }
```

Then add the tests:

```csharp
    [Fact]
    public async Task Final_CleanMerge_Completes()
    {
        var d = new FakeDriver(new[] { Final() });   // MergeResult defaults to Success
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(SteerableState.Completed, state);
        Assert.Equal(1, d.MergeCalls);                       // merge attempted before completing
        Assert.Null(d.ConflictReported);
        Assert.Contains(SteerableState.Completed, d.States);
    }

    [Fact]
    public async Task Final_MergeConflict_RoutesToNeedsRevision_NotCompleted()
    {
        var d = new FakeDriver(new[] { Final() }) { MergeResult = MergeOutcome.Conflict(new[] { "Game/Map.cs" }) };
        var state = await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(SteerableState.NeedsRevision, state);
        Assert.NotNull(d.ConflictReported);
        Assert.Contains("Game/Map.cs", d.ConflictReported!.ConflictFiles);
        Assert.DoesNotContain(SteerableState.Completed, d.States);   // never falsely marked done
    }

    [Fact]
    public async Task NonFinal_Terminal_DoesNotMerge()
    {
        var d = new FakeDriver(new[] { Revision("fix schema") });   // ends NeedsRevision via request_revision
        await SteerableRunCore.RunLoopAsync(d, seed: "go", maxRounds: 10);
        Assert.Equal(0, d.MergeCalls);                        // only the Final path merges
    }
```

- [ ] **Step 4: Run the tests — verify they fail to compile / fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~SteerableRunCoreTests" -v q`
Expected: compile error `'FakeDriver' does not implement 'IRoundDriver.MergeToMainAsync'` until Step 3's members are in, then FAIL — `Final_*` assert on `MergeCalls`/state that the loop doesn't yet drive.

- [ ] **Step 5: Wire the merge into the `Final` case** — replace lines 52-54 of `SteerableRunCore.cs`:

```csharp
                case RoundKind.Final:
                    // Fold this run's files into the board main line BEFORE declaring success. A merge
                    // conflict with concurrent work means we cannot cleanly complete — route to revision
                    // (the task lands in Review) rather than clobbering another task's work or lying "done".
                    var merge = await driver.MergeToMainAsync();
                    if (!merge.Ok)
                    {
                        await driver.OnMergeConflictAsync(merge, outcome);
                        return SteerableState.NeedsRevision;
                    }
                    await driver.OnStateAsync(SteerableState.Completed, outcome);
                    return SteerableState.Completed;
```

- [ ] **Step 6: Run the full test project — verify GREEN**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj -v q`
Expected: PASS (the 3 new + all existing `SteerableRunCoreTests` still green — they now call `MergeToMainAsync`, which defaults to `Success`).

- [ ] **Step 7: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/MergeOutcome.cs \
        src/workflows/Orchestrators/SteerableRunCore.cs \
        tests/TectikaAgents.Tests/SteerableRunCoreTests.cs
git commit -m "feat(workspace): merge run branch into board main on completion (loop seam)"
```

---

## Task 2: `WorkspaceService.MergeRunBranchAsync` + `ParseMerge`

**Files:**
- Modify: `src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs`
- Modify: `src/workflows/Services/WorkspaceService.cs`
- Test: `tests/TectikaAgents.Tests/WorkspaceServiceMergeParseTests.cs` (create)

The executor returns `{"merged":true,"commit":"<sha>"}` on success or `{"merged":false,"conflict":true,"files":["a.cs",...]}` on conflict. `ParseMerge` is a pure, public static helper so it can be TDD'd without HTTP.

- [ ] **Step 1: Write the failing parse tests** — `tests/TectikaAgents.Tests/WorkspaceServiceMergeParseTests.cs`:

```csharp
using TectikaAgents.Workflows.Services;
using Xunit;

public class WorkspaceServiceMergeParseTests
{
    [Fact]
    public void Merged_True_IsOk()
    {
        var r = WorkspaceService.ParseMerge("{\"merged\":true,\"commit\":\"abc123\"}");
        Assert.True(r.Ok);
        Assert.Empty(r.ConflictFiles);
    }

    [Fact]
    public void Conflict_CarriesFiles_NotOk()
    {
        var r = WorkspaceService.ParseMerge("{\"merged\":false,\"conflict\":true,\"files\":[\"Game/Map.cs\",\"Program.cs\"]}");
        Assert.False(r.Ok);
        Assert.Equal(new[] { "Game/Map.cs", "Program.cs" }, r.ConflictFiles);
    }

    [Fact]
    public void Conflict_NoFilesArray_NotOk_EmptyList()
    {
        var r = WorkspaceService.ParseMerge("{\"merged\":false,\"conflict\":true}");
        Assert.False(r.Ok);
        Assert.Empty(r.ConflictFiles);
    }
}
```

- [ ] **Step 2: Run — verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~WorkspaceServiceMergeParseTests" -v q`
Expected: compile error — `WorkspaceService.ParseMerge` does not exist.

- [ ] **Step 3: Add the contract** to `IWorkspaceService.cs` (after `RemoveWorktreeAsync`, ~line 19):

```csharp
    /// <summary>Fold a run's branch into the board main line (commit worktree → merge → push if a
    /// remote exists). Returns Ok on a clean merge, or the conflicting files on a conflict
    /// (the merge is aborted and main is left untouched).</summary>
    Task<MergeOutcome> MergeRunBranchAsync(string endpoint, string token, string runId, CancellationToken ct = default);
```

- [ ] **Step 4: Implement in `WorkspaceService.cs`** — add after `RemoveWorktreeAsync` (~line 176). Confirm `using System.Linq;` and `using System.Text.Json;` are present at the top (they are — `InvokeAsync` uses `JsonSerializer`).

```csharp
    public async Task<MergeOutcome> MergeRunBranchAsync(
        string endpoint, string token, string runId, CancellationToken ct = default)
    {
        var json = await InvokeAsync(endpoint, token, "/worktree/merge", new { run_id = runId }, ct);
        return ParseMerge(json);
    }

    /// <summary>Parse the executor /worktree/merge response into a MergeOutcome. Public + static so it
    /// is unit-testable without HTTP.</summary>
    public static MergeOutcome ParseMerge(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True)
            return MergeOutcome.Success();
        var files = root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();
        return MergeOutcome.Conflict(files);
    }
```

Add `using TectikaAgents.Core.Models;` to `WorkspaceService.cs` if not already imported (it returns `MergeOutcome`).

- [ ] **Step 5: Add the stub to every other `IWorkspaceService` implementer** so the solution compiles. Find them:

Run: `grep -rln ": IWorkspaceService\|IWorkspaceService$" src tests --include=*.cs`
Expected implementers besides `WorkspaceService`: any test/no-op double (e.g. an `UnusedWorkspaceService`). For each, add:
```csharp
    public Task<MergeOutcome> MergeRunBranchAsync(string endpoint, string token, string runId, CancellationToken ct = default)
        => throw new NotImplementedException();
```
(For a fake that must not throw in its scenario, return `Task.FromResult(MergeOutcome.Success())` instead — match the file's existing convention.)

- [ ] **Step 6: Run — verify GREEN**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~WorkspaceServiceMergeParseTests" -v q`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs \
        src/workflows/Services/WorkspaceService.cs \
        tests/TectikaAgents.Tests/WorkspaceServiceMergeParseTests.cs
# plus any implementer stubs you touched in Step 5
git commit -m "feat(workspace): MergeRunBranchAsync + merge-response parsing"
```

---

## Task 3: `MergeRunBranchActivity`

**Files:**
- Create: `src/workflows/Activities/MergeRunBranchActivity.cs`

Durable activity mirroring `PersistWorkspaceActivity`'s shape. No unit test (Durable activity + Cosmos); verified by build + the Task 7 smoke. It is thin: look up the run, call the service, return the `MergeOutcome`.

- [ ] **Step 1: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;

namespace TectikaAgents.Workflows.Activities;

/// <summary>Folds a completed run's branch into the board main line (commit worktree → merge → push if
/// a remote exists). Returns the <see cref="MergeOutcome"/> to the orchestrator, which routes a conflict
/// to NeedsRevision. Called from the loop's Final path BEFORE the run is marked Completed, so a conflict
/// never produces a Completed→NeedsRevision flip-flop.</summary>
public class MergeRunBranchActivity
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly IWorkspaceService _workspace;
    private readonly ILogger<MergeRunBranchActivity> _logger;

    public MergeRunBranchActivity(WorkflowCosmosService cosmos, IWorkspaceService workspace, ILogger<MergeRunBranchActivity> logger)
    {
        _cosmos = cosmos;
        _workspace = workspace;
        _logger = logger;
    }

    [Function(nameof(MergeRunBranchActivity))]
    public async Task<MergeOutcome> Run([ActivityTrigger] string runId, FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;
        var run = await _cosmos.GetRunByIdAsync(runId, ct);
        if (run?.WorkspaceEndpoint is null || run.WorkspaceToken is null)
        {
            // No sandbox was provisioned this run (e.g. a pure-document run) → nothing to fold. Treat as
            // a clean merge so the run completes normally.
            _logger.LogInformation("[MergeRunBranch] run {RunId} had no sandbox — nothing to merge", runId);
            return MergeOutcome.Success();
        }

        var runIdShort = runId[..Math.Min(8, runId.Length)];
        var result = await _workspace.MergeRunBranchAsync(run.WorkspaceEndpoint, run.WorkspaceToken, runIdShort, ct);
        if (result.Ok)
            _logger.LogInformation("[MergeRunBranch] run={RunId} merged into board main", runId);
        else
            _logger.LogWarning("[MergeRunBranch] run={RunId} merge conflict in: {Files}", runId, string.Join(", ", result.ConflictFiles));
        return result;
    }
}
```

- [ ] **Step 2: Build — verify it compiles**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj -c Release -v q`
Expected: `Build succeeded. 0 Error(s)`. (Confirm `WorkflowCosmosService.GetRunByIdAsync` exists — `PersistWorkspaceActivity` uses it.)

- [ ] **Step 3: Commit**

```bash
git add src/workflows/Activities/MergeRunBranchActivity.cs
git commit -m "feat(workspace): MergeRunBranchActivity"
```

---

## Task 4: Wire `DurableRoundDriver` to the activity

**Files:**
- Modify: `src/workflows/Orchestrators/SteerableAgentOrchestrator.cs`

- [ ] **Step 1: Implement the two driver methods** — add to the `DurableRoundDriver` class (after `OnStateAsync`, before `OnExhaustedAsync`). Confirm `using TectikaAgents.Core.Models;` is present (the file already uses `RunStatus`, `RunFailureClass`).

```csharp
        public async Task<MergeOutcome> MergeToMainAsync()
            => await _ctx.CallActivityAsync<MergeOutcome>(nameof(MergeRunBranchActivity), _in.RunId);

        public async Task OnMergeConflictAsync(MergeOutcome merge, RoundOutcome? last)
        {
            var files = merge.ConflictFiles.Count > 0 ? string.Join(", ", merge.ConflictFiles) : "shared files";
            var reason = $"This run's changes could not be merged into the board — they conflict with " +
                         $"concurrent work in: {files}. The task is in Review; re-run it to rebase on the " +
                         $"latest board state, or resolve manually.";
            // NeedsRevision → task Review. An implementation task has no outgoing QaFeedback edge, so this
            // does NOT trigger the upstream QA loop (TryTriggerQaLoopAsync is a no-op without that edge).
            await _ctx.CallActivityAsync(nameof(UpdateRunStatusActivity),
                new UpdateRunStatusInput(_in.RunId, _in.TaskId, _in.BoardId, RunStatus.NeedsRevision, null,
                    ErrorMessage: reason));
        }
```

- [ ] **Step 2: Build — verify it compiles**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj -c Release -v q`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Run the full suite — nothing regressed**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj -v q`
Expected: PASS (all green).

- [ ] **Step 4: Commit**

```bash
git add src/workflows/Orchestrators/SteerableAgentOrchestrator.cs
git commit -m "feat(workspace): drive merge-to-main + conflict→NeedsRevision from DurableRoundDriver"
```

---

## Task 5: Executor `POST /worktree/merge` (Python)

**Files:**
- Modify: `docker/workspace-executor/executor.py`

No Python unit harness in this repo — verified in Task 7. The op: commit the run worktree, merge its branch into the board main-line branch (whatever `/workspace/main` is on — robust to `main`/`master`), abort + report files on conflict, push if an `origin` remote exists. Serialized with a process lock (one container per board ⇒ one process).

- [ ] **Step 1: Add a module-level merge lock** — near the top of `executor.py` (after imports), add:

```python
import threading
_MERGE_LOCK = threading.Lock()   # serialize merges into /workspace/main (one container per board)
```

- [ ] **Step 2: Register the route** — in the POST route table (where `"/worktree/add"` / `"/worktree/remove"` are mapped, ~line 116), add:

```python
            "/worktree/merge": self._handle_worktree_merge,
```

- [ ] **Step 3: Implement the handler** — add alongside `_handle_worktree_add` (~line 261). `WORKSPACE_MAIN` / `WORKSPACE_RUNS` constants already exist (used by the worktree handlers).

```python
    def _handle_worktree_merge(self, body):
        run_id = body.get("run_id", "")
        if not run_id:
            return {"error": "run_id required"}
        branch = f"agent/{run_id}"
        worktree = os.path.join(WORKSPACE_RUNS, run_id)

        with _MERGE_LOCK:
            # 1) Commit any uncommitted work in the run's worktree so the branch is complete.
            if os.path.isdir(worktree):
                subprocess.run(["git", "add", "-A"], cwd=worktree)
                subprocess.run(
                    ["git", "commit", "-m", f"agent run {run_id}"],
                    cwd=worktree)  # no-op (nonzero) if nothing staged — fine

            # 2) Merge the run branch into whatever branch /workspace/main is on (main or master).
            target = subprocess.run(
                ["git", "branch", "--show-current"],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip() or "main"
            merge = subprocess.run(
                ["git", "merge", "--no-ff", "-m", f"merge {branch}", branch],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True)

            if merge.returncode != 0:
                # Conflict (or merge error) — collect conflicted paths, then abort so main is untouched.
                files = subprocess.run(
                    ["git", "diff", "--name-only", "--diff-filter=U"],
                    cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.split()
                subprocess.run(["git", "merge", "--abort"], cwd=WORKSPACE_MAIN)
                return {"merged": False, "conflict": True, "files": files,
                        "detail": (merge.stderr or merge.stdout).strip()[:500]}

            commit = subprocess.run(
                ["git", "rev-parse", "HEAD"],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip()

            # 3) Push the advanced main line if a remote exists (best-effort; main local is the truth).
            has_remote = subprocess.run(
                ["git", "remote"], cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip()
            if has_remote:
                push = subprocess.run(
                    ["git", "push", "origin", f"HEAD:refs/heads/{target}"],
                    cwd=WORKSPACE_MAIN, capture_output=True, text=True)
                if push.returncode != 0:
                    # Non-fatal: a connected remote is a mirror, not the source of truth.
                    sys.stderr.write(f"[worktree/merge] push failed: {push.stderr.strip()}\n")

            return {"merged": True, "commit": commit}
```

- [ ] **Step 4: Document the op in the module docstring** — under the `/worktree/remove` line (~line 19):

```
POST /worktree/merge  {"run_id": "abc12345"}
                      -> {"merged": true, "commit": "<sha>"}
                       | {"merged": false, "conflict": true, "files": ["a.cs", ...], "detail": "..."}
```

- [ ] **Step 5: Syntax-check**

Run: `python3 -c "import ast; ast.parse(open('docker/workspace-executor/executor.py').read()); print('ok')"`
Expected: `ok`.

- [ ] **Step 6: Commit**

```bash
git add docker/workspace-executor/executor.py
git commit -m "feat(executor): /worktree/merge — commit, merge into main, push, conflict-abort"
```

---

## Task 6: Standalone `git init` in `entrypoint.sh`

**Files:**
- Modify: `docker/workspace-executor/entrypoint.sh`

- [ ] **Step 1: Replace the no-repo branch** — swap the `else` block (lines 40-42) for:

```bash
else
    echo "[entrypoint] standalone sandbox (no repo) — initializing local git at /workspace/main"
    git config --global user.email "agent@tectika.com"
    git config --global user.name "Tectika Agent"
    git config --global init.defaultBranch main
    cd /workspace/main
    git init -b main
    git commit --allow-empty -m "init: standalone board workspace"
    echo "[entrypoint] ready on $(git branch --show-current) at /workspace/main (no remote)"
fi
```

(The connected branch already sets `user.email`/`user.name` globally; the standalone branch now sets them too so commits + the empty initial commit succeed. The empty commit gives `main` a base so `git worktree add -b agent/<run>` works.)

- [ ] **Step 2: Lint the script**

Run: `bash -n docker/workspace-executor/entrypoint.sh && echo ok`
Expected: `ok`.

- [ ] **Step 3: Commit**

```bash
git add docker/workspace-executor/entrypoint.sh
git commit -m "feat(executor): git-init a local repo when no remote is connected"
```

---

## Task 7: Build, deploy the workspace image, and smoke-verify end to end

**Files:** none (verification + deploy). The workspace image is a **separate 4th deploy surface** — Tasks 5–6 don't take effect until it's rebuilt.

- [ ] **Step 1: Full solution build + test (regression gate)**

Run: `dotnet build TectikaAgents.sln -c Release -v q && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj -v q`
Expected: build `0 Error(s)`; tests all PASS.

- [ ] **Step 2: Rebuild the workspace ACI image** (per `[[workspace-image-deploy]]`)

Run: `az acr build -r tacragentteam -t agent-workspace:latest docker/workspace-executor/`
Expected: build + push succeeds. (Existing board containers must be recycled to pick it up — they self-destroy after 10 min idle, or delete `tws-<board8>` to force a fresh provision.)

- [ ] **Step 3: Smoke — standalone (no repo) handoff.** On a board with **no** connected repo: run a task that writes a file (e.g. a planner that `write_file`s `PLAN.md` and finishes), then a downstream task that reads it.
  - Verify the first run reaches `Completed` (App Insights `[MergeRunBranch] ... merged into board main`).
  - Verify `tws-<board8>`'s `/workspace/main` is a git repo on `main` with the merge commit: `az container exec ... -- git -C /workspace/main log --oneline` shows `merge agent/<run>`.
  - Verify the downstream run's worktree (cut after the merge) contains `PLAN.md`.

- [ ] **Step 4: Smoke — conflict path.** Force two runs to edit the same line of the same file (e.g. two tasks both rewriting `README.md` line 1), completing close together.
  - The second to merge must end `NeedsRevision`, the task in **Review**, with the "conflict … in: README.md" message — and `git -C /workspace/main log` must show the first run's merge but **not** the second (main untouched by the conflict).

- [ ] **Step 5: Smoke — connected repo unchanged.** On a board **with** a repo, a completing run still merges to `main` and pushes (origin `main` advances). Confirms no regression to the connected path.

- [ ] **Step 6: Final commit (if any smoke fixes were needed) + summary**

```bash
git add -A && git commit -m "fix(workspace): S1 smoke fixes" || echo "no fixes needed"
```

---

## Self-review notes

- **Spec coverage:** standalone `git init` (Task 6) ✓; merge-back on `Completed` only (Task 1 — only the `Final` case calls `MergeToMainAsync`) ✓; serialized merges (Task 5 `_MERGE_LOCK`) ✓; conflict → `NeedsRevision`, main untouched (Tasks 1+4+5) ✓; remote = push mirror, non-fatal push (Task 5) ✓; merge into the actual main-line branch name (Task 5 `git branch --show-current`) ✓.
- **Out of scope (later sub-projects), intentionally not here:** `declare_output` file links (S2), prompt contract (S3), Files-tab UI + `github_list_files` reconciliation (S4). Deliverables still hand off via today's artifact text — but are now **reachable on main**, which is the part that breaks the doom loop.
- **Known edge:** if the conflicting task is itself a *validator* (has an outgoing QaFeedback edge), `NeedsRevision` would trigger the upstream QA loop. Validators rarely write conflicting files; revisit in S3 if observed.
- **TDD honesty:** Tasks 1–2 are real RED→GREEN. Tasks 3–4 are build-verified (thin Durable glue). Tasks 5–6 are Python/Bash with no repo harness → smoke-verified in Task 7; this is stated, not hidden.
