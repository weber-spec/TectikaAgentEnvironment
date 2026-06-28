# S1 — No-Repo Local Merge + Conflict→Review (reconciled) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the merge-back pipeline work on boards with **no connected repo** (the case `929c600` skips), and route **all** merge conflicts to **Review** with a clear message — keeping the just-shipped GitHub-API merge for connected repos.

**Architecture (reconciled 2026-06-28):** A teammate's `929c600` already merges a completed run's branch into the repo default branch **server-side via the GitHub API** for connected boards (origin = truth; worktrees refresh from origin). This plan is the **hybrid** other half: the board workspace becomes git even with no remote (`entrypoint.sh` `git init`), and a completed run on a **no-repo** board folds its branch into the **local** `/workspace/main` via a new executor `/worktree/merge` op — invoked from the `board.GitHub is null` branch of the existing `MergeCompletedBranchToBaseAsync`. Both the connected and local conflict paths now mark the task **Review** (NeedsRevision) with a surfaced message, replacing the shipped `Blocked`+failure.

**Tech Stack:** C# / .NET 10 (Durable Functions, xUnit), Python 3 (ACI executor), Bash. Spec: `docs/superpowers/specs/2026-06-28-unified-deliverable-model-design.md`. Builds on `main@929c600`.

---

## File structure

| File | Responsibility | Change |
|---|---|---|
| `src/core/TectikaAgents.Core/Models/WorkspaceMergeResult.cs` | local-merge result DTO (distinct from GitHub `MergeOutcome` enum) | **create** |
| `src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs` | `MergeRunBranchAsync` contract | modify |
| `src/workflows/Services/WorkspaceService.cs` | merge POST + `ParseMerge` | modify |
| `tests/TectikaAgents.Tests/WorkspaceServiceMergeParseTests.cs` | `ParseMerge` unit tests | **create** |
| `docker/workspace-executor/executor.py` | `POST /worktree/merge` (local) + merge lock | modify |
| `docker/workspace-executor/entrypoint.sh` | `git init -b main` when no remote | modify |
| `src/workflows/Activities/UpdateRunStatusActivity.cs` | no-repo local merge + both conflicts → Review | modify |

Task 1 is real TDD (`ParseMerge`). Task 4 is build-verified (Durable + Cosmos deps). Tasks 2–3 (Python/Bash) have no repo harness → smoke-verified in Task 5. Stated, not hidden.

---

## Task 1: `WorkspaceMergeResult` + `MergeRunBranchAsync` + `ParseMerge`

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/WorkspaceMergeResult.cs`
- Modify: `src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs`, `src/workflows/Services/WorkspaceService.cs`
- Test: `tests/TectikaAgents.Tests/WorkspaceServiceMergeParseTests.cs` (create)

The executor returns `{"merged":true,"commit":"<sha>"}` or `{"merged":false,"conflict":true,"files":[...]}`. **Name it `WorkspaceMergeResult`, NOT `MergeOutcome`** — `929c600` already defines a `MergeOutcome` enum in `TectikaAgents.AgentRuntime.GitHub`.

- [ ] **Step 1: Create the DTO** — `src/core/TectikaAgents.Core/Models/WorkspaceMergeResult.cs`:
```csharp
namespace TectikaAgents.Core.Models;

/// <summary>Result of folding a run's branch into the LOCAL board-workspace main line (no-repo merge,
/// the local analogue of the GitHub-API merge used for connected repos). Ok = main advanced; otherwise a
/// conflict (main untouched) with the conflicting paths for the user-facing Review message.</summary>
public sealed record WorkspaceMergeResult(bool Ok, IReadOnlyList<string> ConflictFiles)
{
    public static WorkspaceMergeResult Success() => new(true, System.Array.Empty<string>());
    public static WorkspaceMergeResult Conflict(IReadOnlyList<string> files) => new(false, files);
}
```

- [ ] **Step 2: Write the failing parse tests** — `tests/TectikaAgents.Tests/WorkspaceServiceMergeParseTests.cs`:
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

- [ ] **Step 3: Run — verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~WorkspaceServiceMergeParseTests" -v q`
Expected: compile error — `WorkspaceService.ParseMerge` does not exist.

- [ ] **Step 4: Add the contract** to `IWorkspaceService.cs` (after `RemoveWorktreeAsync`, ~line 19):
```csharp
    /// <summary>No-repo boards: fold a run's branch into the LOCAL board main line via the executor
    /// /worktree/merge op (commit worktree → merge → conflict-abort). Ok on a clean merge, else the
    /// conflicting files (main left untouched).</summary>
    Task<WorkspaceMergeResult> MergeRunBranchAsync(string endpoint, string token, string runId, CancellationToken ct = default);
```

- [ ] **Step 5: Implement in `WorkspaceService.cs`** — add after `RemoveWorktreeAsync` (~line 176). Confirm `using System.Linq;`, `using System.Text.Json;`, `using TectikaAgents.Core.Models;` are present (add any missing):
```csharp
    public async Task<WorkspaceMergeResult> MergeRunBranchAsync(
        string endpoint, string token, string runId, CancellationToken ct = default)
    {
        var json = await InvokeAsync(endpoint, token, "/worktree/merge", new { run_id = runId }, ct);
        return ParseMerge(json);
    }

    /// <summary>Parse the executor /worktree/merge response. Public + static so it is unit-testable
    /// without HTTP.</summary>
    public static WorkspaceMergeResult ParseMerge(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True)
            return WorkspaceMergeResult.Success();
        var files = root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();
        return WorkspaceMergeResult.Conflict(files);
    }
```

- [ ] **Step 6: Stub the new method on every other `IWorkspaceService` implementer**

Run: `grep -rln "IWorkspaceService" src tests --include=*.cs | xargs grep -l ": IWorkspaceService"`
For each implementer that is NOT `WorkspaceService` (e.g. a test double / `UnusedWorkspaceService`), add:
```csharp
    public Task<WorkspaceMergeResult> MergeRunBranchAsync(string endpoint, string token, string runId, CancellationToken ct = default)
        => throw new NotImplementedException();
```

- [ ] **Step 7: Run — verify GREEN**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~WorkspaceServiceMergeParseTests" -v q`
Expected: PASS (3).

- [ ] **Step 8: Commit**
```bash
git add src/core/TectikaAgents.Core/Models/WorkspaceMergeResult.cs \
        src/core/TectikaAgents.Core/Interfaces/IWorkspaceService.cs \
        src/workflows/Services/WorkspaceService.cs \
        tests/TectikaAgents.Tests/WorkspaceServiceMergeParseTests.cs
# + any implementer stubs from Step 6
git commit -m "feat(workspace): MergeRunBranchAsync (local no-repo merge) + parse"
```

---

## Task 2: Executor `POST /worktree/merge` (local merge)

**Files:** Modify `docker/workspace-executor/executor.py`. Smoke-verified in Task 5.

The op: commit the run worktree, merge its branch into whatever branch `/workspace/main` is on (robust to `main`/`master`), abort + report files on conflict. Serialized with a process lock (one container per board ⇒ one process). No push needed for no-repo, but push if an `origin` exists (harmless for the connected case, which doesn't call this op anyway).

- [ ] **Step 1: Add the merge lock** — near the top of `executor.py`, after the imports (note `subprocess`, `os`, `json` are already imported):
```python
import threading
_MERGE_LOCK = threading.Lock()   # serialize merges into /workspace/main (one container per board)
```

- [ ] **Step 2: Register the route** — in the `do_POST` `dispatch` dict (~line 147, beside `"/worktree/add"`):
```python
            "/worktree/merge": self._handle_worktree_merge,
```

- [ ] **Step 3: Implement the handler** — add alongside `_handle_worktree_add`:
```python
    def _handle_worktree_merge(self, body):
        run_id = body.get("run_id", "")
        if not run_id:
            return {"error": "run_id required"}
        branch = f"agent/{run_id}"
        worktree = os.path.join(WORKSPACE_RUNS, run_id)

        with _MERGE_LOCK:
            if os.path.isdir(worktree):
                subprocess.run(["git", "add", "-A"], cwd=worktree)
                subprocess.run(["git", "commit", "-m", f"agent run {run_id}"], cwd=worktree)  # no-op if nothing staged

            target = subprocess.run(["git", "branch", "--show-current"],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip() or "main"
            merge = subprocess.run(["git", "merge", "--no-ff", "-m", f"merge {branch}", branch],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True)

            if merge.returncode != 0:
                files = subprocess.run(["git", "diff", "--name-only", "--diff-filter=U"],
                    cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.split()
                subprocess.run(["git", "merge", "--abort"], cwd=WORKSPACE_MAIN)
                return {"merged": False, "conflict": True, "files": files,
                        "detail": (merge.stderr or merge.stdout).strip()[:500]}

            commit = subprocess.run(["git", "rev-parse", "HEAD"],
                cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip()
            if subprocess.run(["git", "remote"], cwd=WORKSPACE_MAIN, capture_output=True, text=True).stdout.strip():
                push = subprocess.run(["git", "push", "origin", f"HEAD:refs/heads/{target}"],
                    cwd=WORKSPACE_MAIN, capture_output=True, text=True)
                if push.returncode != 0:
                    print(f"[worktree/merge] push failed: {push.stderr.strip()}", flush=True)  # non-fatal
            return {"merged": True, "commit": commit}
```

- [ ] **Step 4: Document the op** — in the module docstring, under the `/worktree/remove` line:
```
POST /worktree/merge  {"run_id": "abc12345"}
                      -> {"merged": true, "commit": "<sha>"}
                       | {"merged": false, "conflict": true, "files": ["a.cs", ...], "detail": "..."}
```

- [ ] **Step 5: Syntax-check + commit**
```bash
python3 -c "import ast; ast.parse(open('docker/workspace-executor/executor.py').read()); print('ok')"
git add docker/workspace-executor/executor.py
git commit -m "feat(executor): /worktree/merge — local commit+merge into board main, conflict-abort"
```
Expected: `ok`.

---

## Task 3: Standalone `git init` in `entrypoint.sh`

**Files:** Modify `docker/workspace-executor/entrypoint.sh`. Smoke-verified in Task 5.

- [ ] **Step 1: Replace the no-repo `else` block** (the `echo "[entrypoint] standalone sandbox (no repo) at /workspace/main"` branch) with:
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
(The empty commit gives `main` a base so `git worktree add -b agent/<run> main` works; `_refresh_base`'s `git fetch origin` fails gracefully with no remote and leaves the local base in place.)

- [ ] **Step 2: Lint + commit**
```bash
bash -n docker/workspace-executor/entrypoint.sh && echo ok
git add docker/workspace-executor/entrypoint.sh
git commit -m "feat(executor): git-init a local repo when no remote is connected"
```
Expected: `ok`.

---

## Task 4: Wire into `MergeCompletedBranchToBaseAsync` (no-repo local merge + both conflicts → Review)

**Files:** Modify `src/workflows/Activities/UpdateRunStatusActivity.cs`. Build-verified.

- [ ] **Step 1: Inject `IWorkspaceService`** — add to the constructor (already DI-registered in `Program.cs`; `PersistWorkspaceActivity` uses it). Add field `private readonly IWorkspaceService _workspace;`, constructor param `IWorkspaceService workspace`, and `_workspace = workspace;`. Add `using TectikaAgents.Core.Interfaces;` if absent.

- [ ] **Step 2: Replace the no-repo early-return** (the `if (board?.GitHub is null)` block, ~lines 222-226) with a local merge:
```csharp
        var board = await _cosmos.GetBoardAsync(boardId, task.TenantId, ct);
        if (board?.GitHub is null)
        {
            // No remote: deliverable files live only in this board's LOCAL workspace git. Fold the run's
            // branch into local /workspace/main so downstream runs (which fork from it) see them. No
            // CanPushCode gate — there is no origin to write to.
            return await MergeLocalNoRepoAsync(run, boardId, taskId, runId, ct);
        }
```

- [ ] **Step 3: Replace the connected conflict case** (the `case MergeOutcome.Conflict:` block, ~lines 256-262) with Review routing:
```csharp
                case MergeOutcome.Conflict:
                default:
                    _logger.LogWarning("[Merge] conflict merging {Head} → {Base} for task {TaskId} — sending to Review", head, baseBranch, taskId);
                    await MarkMergeConflictReviewAsync(boardId, taskId, runId, run.CurrentStep,
                        new[] { $"{head} ↔ {baseBranch}" }, ct);
                    return false;
```
Also update the method's doc-comment `<returns>` wording from "the task is marked Blocked" → "the task is sent to Review".

- [ ] **Step 4: Add the two helpers** — after `MergeCompletedBranchToBaseAsync`:
```csharp
    /// <summary>No-repo boards: fold the run's branch into local /workspace/main via the executor's
    /// /worktree/merge op (local analogue of the GitHub-API merge). Returns true to allow the downstream
    /// cascade; false on a real conflict (task → Review, message surfaced, main untouched).</summary>
    private async Task<bool> MergeLocalNoRepoAsync(WorkflowRun run, string boardId, string taskId, string runId, CancellationToken ct)
    {
        if (run.WorkspaceEndpoint is null || run.WorkspaceToken is null)
        {
            _logger.LogDebug("[Merge] no repo + no workspace for task {TaskId} — nothing to merge", taskId);
            return true;   // pure-document run; nothing on disk to integrate
        }
        var runIdShort = runId[..Math.Min(8, runId.Length)];
        WorkspaceMergeResult result;
        try { result = await _workspace.MergeRunBranchAsync(run.WorkspaceEndpoint, run.WorkspaceToken, runIdShort, ct); }
        catch (Exception ex)
        {
            // Best-effort, mirror the connected path: a transient executor error must not strand the pipeline.
            _logger.LogError(ex, "[Merge] local merge errored for task {TaskId} — allowing cascade", taskId);
            return true;
        }
        if (result.Ok)
        {
            _logger.LogInformation("[Merge] local merge of {Head} into board main ok for task {TaskId}", runIdShort, taskId);
            return true;
        }
        _logger.LogWarning("[Merge] local merge conflict for task {TaskId} in: {Files}", taskId, string.Join(", ", result.ConflictFiles));
        await MarkMergeConflictReviewAsync(boardId, taskId, runId, run.CurrentStep, result.ConflictFiles, ct);
        return false;
    }

    /// <summary>A merge (server-side or local) hit a conflict: route the task to Review with a clear,
    /// user-visible message rather than silently completing. NeedsRevision → Review; an implementation task
    /// has no outgoing QaFeedback edge, so this does not bounce the upstream QA loop.</summary>
    private async Task MarkMergeConflictReviewAsync(string boardId, string taskId, string runId, int round,
        IReadOnlyList<string> conflictFiles, CancellationToken ct)
    {
        var where = conflictFiles.Count > 0 ? string.Join(", ", conflictFiles) : "shared files";
        var message = $"This task's changes could not be merged — they conflict with concurrent work in: {where}. " +
                      "The task is in Review; re-run it to rebase on the latest, or resolve the conflict manually.";
        await _cosmos.UpdateTaskStatusAsync(boardId, taskId, AgentTaskStatus.Review, runId, ct);
        try
        {
            var ev = new RunEvent { TaskId = taskId, RunId = runId, Round = round,
                Kind = RunEventKind.RevisionRequested, Title = message, Detail = message };
            var saved = await _cosmos.CreateRunEventAsync(ev, ct);
            await _events.PublishRunEventAsync(saved, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Merge] failed to surface conflict event for {TaskId}", taskId); }
    }
```

- [ ] **Step 5: Build + full test (nothing regressed)**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj -c Release -v q && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj -v q`
Expected: build `0 Error(s)`; tests all PASS.

- [ ] **Step 6: Commit**
```bash
git add src/workflows/Activities/UpdateRunStatusActivity.cs
git commit -m "feat(workflows): no-repo local merge on completion; merge conflicts → Review (both modes)"
```

---

## Task 5: Build, deploy the workspace image, smoke-verify

**Files:** none (verify + deploy). Tasks 2–3 don't take effect until the workspace image is rebuilt (separate 4th surface).

- [ ] **Step 1: Full solution build + test gate**

Run: `dotnet build TectikaAgents.sln -c Release -v q && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj -v q`
Expected: build `0 Error(s)`; tests all PASS.

- [ ] **Step 2: Rebuild the workspace ACI image** (per `[[workspace-image-deploy]]`)

Run: `az acr build -r tacragentteam -t agent-workspace:latest docker/workspace-executor/`
Expected: build + push succeed. Recycle board containers (`tws-<board8>`) to pick it up, or let the 10-min idle cleanup recycle them.

- [ ] **Step 3: Smoke — no-repo handoff.** On a board with **no** connected repo: a task that `write_file`s `PLAN.md` and finishes, then a downstream task that reads it.
  - First run reaches Completed; App Insights `[Merge] local merge ... ok`.
  - `az container exec ... -- git -C /workspace/main log --oneline` shows `merge agent/<run>`.
  - Downstream run's worktree (cut after the merge) contains `PLAN.md`.

- [ ] **Step 4: Smoke — no-repo conflict → Review.** Two no-repo tasks edit the same line of the same file, completing close together. The second to merge lands the task in **Review** with the "conflict … in: <file>" message; `git -C /workspace/main log` shows the first merge but not the second.

- [ ] **Step 5: Smoke — connected conflict now → Review (regression of the policy change).** On a board **with** a repo, force a GitHub-merge conflict: the task lands in **Review** (not Blocked), message surfaced, no downstream cascade.

- [ ] **Step 6: Smoke — connected clean path unchanged.** A non-conflicting completion on a connected board still GitHub-merges to the default branch and cascades downstream.

---

## Self-review notes

- **Spec coverage:** standalone `git init` (Task 3) ✓; no-repo merge-back on completion (Tasks 1–2, 4) ✓; serialized merges (Task 2 `_MERGE_LOCK`) ✓; conflict → Review, main untouched, message surfaced (Task 4, both modes) ✓; merge into the actual main-line branch (`git branch --show-current`, Task 2) ✓.
- **Reconciliation with `929c600`:** connected-repo merge kept as-is (GitHub API, origin = truth); this plan adds only the no-repo local path and flips both conflict policies to Review. The previous loop-based approach (IRoundDriver/MergeRunBranchActivity) is **dropped** — their merge lives in `UpdateRunStatusActivity`, so we extend it there.
- **Naming:** `WorkspaceMergeResult` (Core) is deliberately distinct from `929c600`'s `MergeOutcome` enum (`TectikaAgents.AgentRuntime.GitHub`).
- **Known edge:** if the conflicting task is a *validator* (outgoing QaFeedback edge), Review would trigger the QA loop. Validators rarely write conflicting files; revisit in S3 if seen.
- **Out of scope (later):** `declare_output` links (S2), prompts (S3), Files-tab UI (S4).
