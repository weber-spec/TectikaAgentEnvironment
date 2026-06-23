# Design — Workspace state persistence across runs (QA S1 §2.1)

**Date:** 2026-06-23 · **Status:** Approved (pending spec review) · **Scope:** S1 of the interactive-chat QA fix

## Problem

A task's agent work is silently lost across **run** boundaries. The agent's own
`request_revision` named the symptom: files written in one run are "missing" in the next. Root cause,
confirmed in code:

1. The workspace git branch was **per-run** (`agent/<run8>`), computed in `RunWorkspaceProvider` and
   `TryBuildCodeOutputAsync`. Each run started on its own branch.
2. Each run provisions a **fresh** ACI that does a clean `git clone` (`docker/workspace-executor/entrypoint.sh`).
3. The agent runs only `dotnet`, **never `git`** — so files written via `write_file` lived only on the
   ephemeral ACI disk and died with it (the ACI is destroyed in the orchestrator's `finally`).

`request_human_input` keeps the *same* run (and ACI) alive, so state survives within a run; it is lost
specifically at **run→run** boundaries (revision, round-exhaustion, await-timeout).

## Decision

Persist working state through **git, on an isolated per-task branch in the user's own repo**, with a
**single commit at the run boundary**. Compute stays **disposable per run**.

- **State store = git.** One branch per task, `agent/task-<taskId>`, shared by every run of that task.
  The working tree lives on the ACI's **fast local disk** (good build perf); only committed deltas leave.
- **Persistence point = run end, once.** A new `PersistWorkspaceActivity` runs
  `git add -A && (commit if staged) && git push origin HEAD` in `SteerableAgentOrchestrator`'s `finally`,
  **before** `DestroyWorkspaceActivity`. It fires on every run-end path (Final, NeedsRevision,
  round-exhaustion, await-timeout, error). `request_human_input` keeps the run alive, so it correctly
  does **not** commit mid-conversation.
- **Restore = existing entrypoint.** The entrypoint already re-checks-out an existing branch on fresh
  clone (`git checkout "$GIT_BRANCH"`), so run N+1 restores run N's commits automatically. No entrypoint
  or infra change.
- **Continuation awareness.** On a continuation run the agent is told its prior files are restored on
  the task branch (`ContinuationNote`), so it builds on them instead of starting over.
- **Compute = per-run disposable** (unchanged). Provision on first tool call, destroy at run end.
  **Zero idle cost** — this is what scales to dozens of tasks per board.
- **Delivery (future, §3.3).** The isolated branch is designed to be **squashed into a clean PR** at
  delivery time, so the user's main branches never see WIP. Delivery/PR creation is out of scope for S1.

## Cost / performance rationale (grounded)

- ACI has **no idle auto-termination**; a running group bills until explicitly stopped/deleted. Stopping
  deallocates it (billing stops) **but wipes the local disk** — so any reuse model needs a durable store
  anyway.
- ACI Linux ≈ **$0.0000135/vCPU-s + $0.0000015/GB-s**. Our 1 vCPU / 2 GB box ≈ **$43/month always-on**
  vs **~1¢ of compute per ~10-min run**. Always-on per-task/per-board is therefore far more expensive at
  scale; per-run disposable has **zero idle cost**.
- What cold-start (127–212s/run) actually costs is **latency, not dollars** — so it is attacked
  separately by a **warm pool** (QA §3.1, S2), not by keeping instances alive.

## Alternatives considered & rejected

- **Per-task or per-board always-on ACI.** Eliminates re-clone/cold-start but costs ~$43/mo each (per
  task = hundreds–thousands/mo at scale; per board adds concurrency contention, blast radius, and an
  executor working-dir change) — and still needs a durable backstop vs eviction. Rejected for S1; the
  cold-start latency it targets is handled by a warm pool instead.
- **Azure Files mount as the working tree.** Persists across restarts, but SMB is slow for build
  workloads (`.git`, `node_modules`, `obj/bin` = thousands of tiny files), so `dotnet build`/`npm install`
  would crawl. Git keeps the tree on fast local disk and ships only deltas — the right tool for a *code*
  workspace. Rejected.
- **Separate Tectika-owned state repo (per board/task folders).** Targets "clean user repo / no push
  role," but: (a) stores customer source in our infra — a multi-tenant governance liability; (b) the
  workspace is a *clone of the user's* repo, so a second repo means a cross-repo replay/sync with conflict
  risk; (c) repo bloat + provisioning. Its goals are met more cheaply by an isolated branch + squash-on-
  deliver. Rejected. (If "zero footprint until approval" were a hard requirement, a state *bundle* in blob
  storage would beat a second git repo — not needed: WIP on an isolated branch was accepted.)

## Known limitations (accepted for S1)

- **Non-push roles** can't use the git store (no push permission); persistence won't carry for them. Same
  as today; a durable-volume fallback remains a documented S2 option. The QA agent (Engineer) has
  `CanPushCode`.
- **Mid-run ACI eviction** loses that run's uncommitted work (the run fails; the next run resumes from the
  last boundary commit). Rare; not worth per-round commits.
- **Code-artifact diff on the Final run** (§3.3, S2) misses the very last commit, since it's built before
  the `finally`. A seam to close when §3.3 lands.

## Adjacent S1 work (already implemented, not part of this decision)

QA §2.2 over-asking fixes ship alongside: non-interactive-sandbox constraint + autonomy guidance in
`WorkspacePrompt`; tightened `request_human_input` description + `TectikaToolSchema.Version` bump (so
deployed agents republish via `AgentSelfHeal`); and a per-task repeat-ask guard (`RepeatAskGuard` +
`AgentTask.HumanAskCount`) that converts a 3rd+ `request_human_input` into autonomous continuation.

## Verification

Deploy (manual `az`/`func`) then re-run the Windows QA harness: a task spanning ≥2 runs shows run N's
files present in run N+1's `/list` and a successful `dotnet build`; the agent makes no interactive-TTY
choice and raises 0 self-bug `request_human_input`. Confirm the deployed agent role has `CanPushCode`.
