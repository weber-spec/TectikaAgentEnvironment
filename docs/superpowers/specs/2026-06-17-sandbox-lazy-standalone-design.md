# On-demand, standalone (git-isolated) sandbox — Design

**Date:** 2026-06-17
**Status:** Approved for planning (architecture + git-isolation confirmed via Q&A)
**Branch:** `feat/sandbox-lazy-standalone` (off `main`)

## Problem / goals

The agent sandbox (an ACI "workspace") today is **eager** (provisioned at run start) and **repo-bound** (only when the board has a GitHub repo; the container always clones the repo and configures git). Three changes:

1. **Lazy / on-demand** — provision the sandbox only the **first time the agent calls `run_command`**, reuse it for the rest of the run, destroy it at run end. A run that never uses the terminal costs nothing.
2. **Standalone** — provision a sandbox even when **no repo** is connected.
3. **Git-isolated** — when there's no repo, the container does **not** clone or `git init` anything (bare `/workspace`), and the prompts + `run_command` description must **not** claim git is ready. When a repo *is* connected, behavior is unchanged (clone + checkout + git configured).

These move **together** so the agent is never told git is ready when it isn't.

## Inspection — every git/workspace touchpoint (and its fix)

| Touchpoint | Today | Change |
|---|---|---|
| `docker/workspace-executor/entrypoint.sh` | `set -u`; unconditionally clones `$REPO_URL`, checks out `$GIT_BRANCH` | `if [ -n "${REPO_URL:-}" ]` → clone + checkout (unchanged); **else** → `mkdir -p /workspace`, **no git**, start executor |
| `src/workflows/Services/WorkspaceService.cs` | returns `null` if `board.GitHub is null`; always sets `REPO_URL`/`GIT_BRANCH`/`GIT_PAT` | provision in **both** cases; set repo env vars **only when a repo is connected** (omit `REPO_URL`/`GIT_BRANCH`/`GIT_PAT` for standalone) |
| Round-0 prompt `RunAgentRoundActivity` | "repo cloned and ready at `/workspace` … git commit/push" / else "no workspace, you cannot run commands" | always advertise an **on-demand** terminal; describe git as ready **only when a repo is connected**; never say "you cannot run commands" |
| `run_command` desc `TectikaToolSchema` | "the cloned git repo … working dir is the repo root … git commit/push" | generic: "Run a bash command in the sandbox (`/workspace`). With a repo connected it's cloned there and git is configured; otherwise it's an empty sandbox." |
| `executor.py` docstring / `Dockerfile` comment | "the cloned repo" / "clones repo" | de-git-ify the comments; `/workspace` always exists |

`github_*` tool descriptions ("relative to repo root") are about the GitHub **API**, not the sandbox — left unchanged.

## Architecture — lazy provisioning

**Mechanism: a workspace *provider* abstraction** (cleaner than splitting the round, same effect — the agentruntime stays a pure executor and just asks for a workspace when it needs one, exactly like it already depends on `IProjectExplorer`/`IGitHubToolExecutor`).

- **New abstraction** (core): `IWorkspaceProvider { Task<WorkspaceConnection?> EnsureAsync(CancellationToken ct); }` and `record WorkspaceConnection(string Endpoint, string Token)`. `RoundRequest` carries an optional `IWorkspaceProvider? Workspace` instead of the static `WorkspaceEndpoint`/`WorkspaceToken` strings.
- **Executor**: in `RoundExecutor`, the `run_command` case calls `req.Workspace?.EnsureAsync(ct)`; if it returns a connection → execute via `WorkspaceToolExecutor`; if it returns `null` (no provider, e.g. compact path) → the existing graceful "no workspace" tool error. The provider caches within a round so multiple `run_command`s in one round provision once.
- **Provider implementation** (workflows, in `RunAgentRoundActivity`): `EnsureAsync` →
  1. read the run doc; if `run.WorkspaceEndpoint` is set (provisioned by an earlier round) → return it;
  2. else call `WorkspaceService.ProvisionAsync(board, …)` (now standalone-capable), persist `WorkspaceEndpoint`/`WorkspaceToken`/`WorkspaceContainerName` to the run doc, and return.
  So provisioning happens **once per run**, lazily, and is reused across rounds via the run document.
- **Run document** (core): add `WorkspaceEndpoint` + `WorkspaceToken` to `WorkflowRun` (alongside the existing `WorkspaceContainerName`); extend the patch helper.
- **Orchestrator** (`SteerableAgentOrchestrator`): **remove** the eager `ProvisionWorkspaceActivity` at start. Keep a **destroy in `finally`** — a cleanup activity that reads the run's `WorkspaceContainerName` and tears it down **only if set** (no-op otherwise). `ProvisionWorkspaceActivity` is removed/retired.

## Prompt text (round 0)

Always include a `## Workspace` block:
- **Repo connected:** "You have an on-demand sandbox terminal via `run_command`. On first use, the connected GitHub repository is cloned to `/workspace` with git configured (you can `git commit`/`git push`)."
- **Standalone:** "You have an on-demand sandbox terminal via `run_command` — an empty `/workspace` (no git repo connected). Use it to write and run code."

(The agent always knows it *has* a terminal; the sandbox is created on first `run_command`.)

## Files touched

- **docker/** `workspace-executor/entrypoint.sh` (conditional git), `executor.py` + `Dockerfile` (comments).
- **core** `Models/WorkflowRun.cs` (+`WorkspaceEndpoint`/`WorkspaceToken`), `Interfaces/IWorkspaceProvider.cs` (new), `Models/RoundContracts.cs` (`RoundRequest.Workspace`).
- **agentruntime** `RoundExecutor.cs` (call the provider on `run_command`), `FoundryAgentRuntime.cs` (pass `req.Workspace` through), `TectikaToolSchema.cs` (`run_command` desc), `AgentToolLoop.cs` (provider plumbing; compact path passes `null`).
- **workflows** `WorkspaceService.cs` (standalone provisioning), `Activities/RunAgentRoundActivity.cs` (provider impl + round-0 prompt), `Services/WorkflowCosmosService.cs` (patch endpoint/token), `Orchestrators/SteerableAgentOrchestrator.cs` (drop eager provision; conditional destroy), retire `ProvisionWorkspaceActivity`.

## Testing

- **Unit:** `WorkspaceService` builds repo env vars only when a repo is present (extract the env-var assembly into a testable helper); the round-0 prompt builder returns the right text for repo vs standalone (extract to a pure helper + test); the provider returns the cached/run-doc connection without re-provisioning.
- **Image:** `entrypoint.sh` — a shell test (or manual) that with `REPO_URL` empty it makes `/workspace` and starts the executor without touching git.
- **Backend:** `dotnet test`; **web** unaffected.
- **Deploy:** rebuild + push the `agent-workspace` image to ACR; deploy **workflows**. (No API/web change.)

## Edge cases

- **Run never calls `run_command`** → no ACI ever created; destroy activity is a no-op. (The cost win.)
- **Provisioning fails** at first `run_command` → `run_command` returns a clear error to the agent; the run continues (no crash). The destroy activity still no-ops (nothing to clean) unless a container name was persisted.
- **Multiple `run_command`s across rounds** → provisioned once (run-doc reuse), destroyed once at run end.
- **Compact / summarize path** (`RunTurnAsync`/`AgentToolLoop` with no provider) → `run_command` unavailable, as today.
- **Standalone image** must `mkdir -p /workspace` before `exec`ing the executor (executor's `cwd=/workspace`).
