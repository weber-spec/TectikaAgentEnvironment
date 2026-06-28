# Unified Deliverable Model — Design

**Status:** approved-architecture, S1 detailed
**Date:** 2026-06-28
**Branch:** `feat/unified-deliverable-model`

## Problem

A real run (`task 3d2152b4` "Implement the backend") doom-looped: a QA validator kept
requesting revision of an upstream "Plan the development" task because
`docs/DevelopmentPlan.md` was "missing from the repo", hit the 3-attempt cap, and the task
was Blocked. Root causes, all verified in code + Cosmos:

1. **Git is used as the inter-task handoff channel, and it structurally cannot be one.**
   Each run gets its own worktree branch `agent/<run8>` cut from `/workspace/main`
   (`docker/workspace-executor/executor.py` `git worktree add -b`), and **nothing merges those
   branches back**. The planner wrote the plan file on `agent/342e3c0a`; the validator looked
   on `agent/54f38f89`. The file is permanently unreachable across runs.
2. **The real deliverable channel (Cosmos artifacts) works but is ignored.** The plan WAS a
   declared output, auto-injected into the validator's context
   (`RunAgentRoundActivity` → `ContextManager.Assemble`). The validator read it, then validated
   against repo files anyway — because the prompts frame the repo as the deliverable, and the
   plan artifact's own text pointed at repo paths.
3. **No-repo boards have no isolation at all.** `entrypoint.sh`: with a repo → clone to
   `/workspace/main`; **without a repo → "a bare /workspace/main (no clone, no git init)"** — a
   plain folder. The per-run-worktree model only works because a repo happens to be present.
   No-repo boards have every task writing the same shared directory with no conflict handling.

A linked repo is **optional** — just an extra tool. Everything must work without one.

## Principles

- The repo is optional; the system is fully functional with **no** remote connected.
- **Two separated layers:**
  - **Deliverable record** — the Cosmos artifact: a summary plus **links** to the deliverable
    files (and inline content for pure prose). This is the handoff object downstream tasks read.
  - **Deliverable files** — the real files, living in the board **workspace** (always) and
    **also** in the remote repo when one is connected.
  - The record's links point at the deliverable files; the UI resolves them.
- **The workspace is always a git repo** (the unifying substrate). Connected → clone of the
  remote; standalone → a locally `git init`-ed repo. The remote is purely an optional push
  target. This makes isolation, conflict-merge, and versioned/linkable files work **identically**
  in both modes via the existing per-run-worktree machinery.
- **Inter-task handoff = the record + the board's merged main line**, never a sibling run branch.
- Conflicts are managed by git merge, **serialized per board**, and **surfaced as
  `NeedsRevision`** — never a silent clobber.

## The unified model (architecture)

```
Cosmos                         Board workspace (ACI, always git)         Remote repo (optional)
──────                         ─────────────────────────────────        ─────────────────────
Deliverable record  ──links──▶ /workspace/main  (the board "main line") ──push (if connected)──▶ origin/main
(artifact: summary             ▲  merge-back on run success
 + file links + inline)        │
                               └ /workspace/runs/<run>  (agent/<run> worktree, branched off main)

Files tab (UI): browses the board main line — repo contents if connected, else the workspace
folder. Artifact links deep-link into it.
```

- **Layer 1 — Deliverable record (Cosmos artifact).** Gains a `links` collection: each link is a
  workspace-relative file path (optionally a line range), resolved against the board main line.
  Pure-prose deliverables still carry inline content. (Detailed in **S2**.)
- **Layer 2 — Deliverable files (workspace git).** Agents write files into their run worktree and
  commit them. On run success the worktree branch merges into `/workspace/main`. If a remote is
  connected, main is pushed.
- **Substrate.** `/workspace/main` is always a git repo. Two provisioning modes:
  - *Connected:* `git clone <remote>` (today's behaviour).
  - *Standalone:* `git init` + an initial commit on `main` (NEW — this is S1's core).
- **Pipeline.** A run works on `agent/<run>` cut from main. On `Completed`, a **serialized**
  merge-back folds it into main; conflicts abort the merge and mark the run `NeedsRevision`.
- **Links & UI.** `declare_output` records links; the Files tab browses the main line; links are
  clickable. (Detailed in **S2/S4**.)
- **Prompts.** Deliver via `declare_output` with links to the files you wrote; validators validate
  the record + its linked files on the main line; never assume a remote repo. (Detailed in **S3**.)

## Decomposition (sub-projects, in build order)

Each is its own spec → plan → implement cycle. Issue #1 (`ReviewNotConverged` failure class) is
already shipped (commit `ea3ee69`).

- **S1 — Git-always substrate + merge-back pipeline.** Backend foundation; without it nothing else
  is reachable cross-task. **Absorbs issue #4** (orphan branches) and the workspace half of issue
  #3 (repo-connection inconsistency). Detailed below.
- **S2 — Deliverable record ↔ file links.** `declare_output` links, artifact model, context
  injection of the links.
- **S3 — Prompts.** The deliverable/validation contract on top of S1+S2. **Absorbs over-asking**
  (issue #3-residual / run-1 behaviour).
- **S4 — Files tab (UI).** Browse repo-or-workspace; clickable artifact links; reconcile the
  `github_list_files` "no repo connected" signal with the always-present workspace.

---

## S1 — Git-always substrate + merge-back pipeline (detailed spec)

### Goal

The board workspace is always a git repo, and a completed run's files become visible to
downstream tasks by merging its branch into the board main line. Real merge conflicts surface as
`NeedsRevision`, never a silent overwrite.

### Components & changes

1. **`docker/workspace-executor/entrypoint.sh` — standalone init.** When `REPO_URL` is unset,
   replace the bare-folder path with: `git init -b main /workspace/main`, set a bot
   `user.name`/`user.email`, and make an empty initial commit (`git commit --allow-empty`) so
   `main` exists as a branch base for `git worktree add`. Connected mode is unchanged.
   *Rebuild the workspace image after (it is a separate deploy surface).*

2. **Merge-back step — NEW.** After a run reaches `Completed`, fold `agent/<run>` into `main` in
   `/workspace/main`, then push `main` if a remote is connected. Mechanics:
   - Executor gains a `POST /worktree/merge {run_id}` op: in `/workspace/main`, `git merge --no-ff
     agent/<run>`; on conflict, `git merge --abort` and return `{merged:false, conflict:true,
     files:[...]}`; on success return `{merged:true, commit:<sha>}` (and `git push origin main` if
     a remote exists, push failure non-fatal).
   - A new workflow activity `MergeRunBranchActivity(runId)` calls it. Invoked from
     `SteerableAgentOrchestrator` **only when the run completed** (`SteerableState.Completed`),
     before `DestroyWorkspaceActivity`. (`PersistWorkspaceActivity` already commits the worktree;
     the merge runs after that.)

3. **Serialization (conflict-safe pipeline).** Merges into a board's `main` must not interleave.
   Serialize per board with a lightweight lock (board-scoped, e.g. an `etag`/optimistic field on
   `Board`, or a single-flight queue keyed by `boardId`). A run waiting for the lock blocks only on
   the merge step, not on its agent work.

4. **Conflict → `NeedsRevision`.** If `/worktree/merge` reports a conflict, the orchestrator marks
   the run `NeedsRevision` with a message naming the conflicting files and the concurrent change,
   and leaves `main` untouched. This reuses the existing `NeedsRevision` → Review status flow.
   Note: an implementation task has **no outgoing QaFeedback edge**, so this does **not** auto-trigger
   the upstream QA loop (`UpdateRunStatusActivity.TryTriggerQaLoopAsync` is a no-op without such an
   edge) — the task simply lands in **Review** for a human to look at or re-run (which rebases on the
   now-advanced main). That re-run is the intended resolution path, not an automatic upstream bounce.

5. **Repo-connection truth (workspace half of #3).** "Connected" is defined as *the workspace has
   an `origin` remote*. The board main line is the single source of truth for files regardless.
   (The `github_list_files`/Files-tab reconciliation is completed in S4.)

### Data flow

```
provision board container
  ├─ connected  → git clone remote      → /workspace/main on `main`
  └─ standalone → git init -b main + empty commit → /workspace/main on `main`
run starts → /worktree/add  (agent/<run> off main) → agent edits + commits in worktree
run Completed → PersistWorkspaceActivity (commit worktree)
            → MergeRunBranchActivity → /worktree/merge
                 ├─ clean   → main advances → push if remote → downstream runs branch off new main
                 └─ conflict→ merge --abort → run marked NeedsRevision (main unchanged)
            → DestroyWorkspaceActivity (idle cleanup unchanged)
```

### Error handling

- `git init` failure on provision → workspace provisioning fails as `SandboxInfra` (existing path).
- Merge conflict → `NeedsRevision` with conflicting-files message; `main` unchanged.
- Remote push failure → non-fatal warning; `main` (local) remains the source of truth; retried
  next merge. (A connected remote can lag; it is a mirror, not the truth.)
- Merge step itself throwing (executor 5xx) → run is **not** marked Completed-with-merge; surfaced
  as a run failure so a stuck pipeline is visible (do not silently skip the merge).

### Testing (TDD)

- **entrypoint (standalone):** provisioning with no `REPO_URL` yields a valid git repo on `main`
  with one commit and a working `git worktree add` (script-level or executor smoke test).
- **Merge orchestration (unit, fake workspace service):**
  - a `Completed` run triggers exactly one merge of its branch into main;
  - a clean merge advances main and (connected) pushes;
  - a **conflict** result → run marked `NeedsRevision`, main untouched, message names files;
  - merges for two runs on the same board are **serialized** (no interleave);
  - non-`Completed` terminal states (Failed/AwaitUser-timeout/NeedsRevision) do **not** merge.
- **Cross-run visibility:** a worktree cut after a prior run's merge sees the merged files.

### Out of scope for S1

`declare_output` links (S2), prompt contract (S3), Files-tab UI + `github_list_files`
reconciliation (S4). S1 ships the substrate and pipeline only; deliverables still hand off via
today's artifact text until S2/S3 land — but they are now **reachable on main**, which is what
breaks the doom loop.

### Decisions captured

- Merge-back fires **only on `Completed`** (partial/failed runs never pollute main).
- Default branch is **`main`** in both modes.
- Merges are **serialized per board**; the exact lock mechanism is chosen in the S1 implementation
  plan.
- The remote, when present, is a **push mirror**; the workspace main line is the source of truth.
