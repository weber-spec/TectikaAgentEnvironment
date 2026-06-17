# On-demand, standalone (git-isolated) sandbox — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`).

**Goal:** Provision the agent sandbox **only when the agent first calls `run_command`** (lazy), make it work **without a connected repo** (standalone), and keep it **git-isolated** (no auto-clone/`git init` when there's no repo) with prompts + tool description that never claim git is ready when it isn't.

**Architecture:** A `IWorkspaceProvider` abstraction supplies a workspace on demand. `RoundExecutor`'s `run_command` asks the provider for an endpoint/token (provisioning lazily on first use); the workflow-side provider provisions the ACI, persists endpoint/token/container on the run doc, and reuses it across rounds. The orchestrator no longer provisions eagerly; it destroys the container at run end only if one was created.

**Spec:** `docs/superpowers/specs/2026-06-17-sandbox-lazy-standalone-design.md`. **Branch/worktree:** `feat/sandbox-lazy-standalone` at `.claude/worktrees/sandbox`.

**All `dotnet`/`git` commands run from the worktree root** `/home/elimeshi/projects/repos/TectikaAgentEnvironment/.claude/worktrees/sandbox`.

---

## Task 1: Core contracts

**Files:** create `src/core/TectikaAgents.Core/Interfaces/IWorkspaceProvider.cs`; modify `src/core/TectikaAgents.Core/Models/RoundContracts.cs`, `src/core/TectikaAgents.Core/Models/WorkflowRun.cs`.

- [ ] **Step 1 — provider abstraction.** Create `IWorkspaceProvider.cs`:
```csharp
namespace TectikaAgents.Core.Interfaces;

/// <summary>A live sandbox connection (executor endpoint + auth token).</summary>
public sealed record WorkspaceConnection(string Endpoint, string Token);

/// <summary>Supplies a sandbox workspace on demand. Implementations provision lazily (first call)
/// and cache, so calling repeatedly within/across a run returns the same workspace. Returns null
/// when no sandbox is available (e.g. provisioning unavailable / the compact path).</summary>
public interface IWorkspaceProvider
{
    Task<WorkspaceConnection?> EnsureAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2 — RoundRequest carries a provider.** In `RoundContracts.cs`, in the `RoundRequest` record, replace the two trailing params:
```csharp
    GitHubRepoConnection? BoardGitHub = null,
    string? WorkspaceEndpoint = null,
    string? WorkspaceToken = null);
```
with:
```csharp
    GitHubRepoConnection? BoardGitHub = null,
    TectikaAgents.Core.Interfaces.IWorkspaceProvider? Workspace = null);
```

- [ ] **Step 3 — WorkflowRun stores endpoint/token.** In `WorkflowRun.cs`, after `WorkspaceContainerName`, add:
```csharp
    [JsonPropertyName("workspaceEndpoint")]
    public string? WorkspaceEndpoint { get; set; }

    [JsonPropertyName("workspaceToken")]
    public string? WorkspaceToken { get; set; }
```

- [ ] **Step 4 — build core:** `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj` → succeeds.
- [ ] **Step 5 — commit:** `git add -A && git commit -m "feat(core): IWorkspaceProvider + run workspace endpoint/token"` (the agentruntime won't compile yet — that's Task 2; core builds alone).

---

## Task 2: agentruntime — provider plumbing + run_command description

**Files:** modify `src/agentruntime/RoundExecutor.cs`, `src/agentruntime/FoundryAgentRuntime.cs`, `src/agentruntime/AgentToolLoop.cs`, `src/agentruntime/TectikaToolSchema.cs`; fix any tests that call the changed signatures.

- [ ] **Step 1 — `RoundExecutor` uses the provider.** In `ExecuteOneRoundAsync`, replace the `WorkspaceToolExecutor? workspace, string? workspaceEndpoint, string? workspaceToken` parameters with `WorkspaceToolExecutor? workspace, TectikaAgents.Core.Interfaces.IWorkspaceProvider? workspaceProvider`. In the `case "run_command":` branch, replace the endpoint/token check with:
```csharp
                case "run_command":
                    var conn = workspaceProvider is null ? null : await workspaceProvider.EnsureAsync(ct);
                    if (workspace is not null && conn is not null)
                    {
                        var wsResult = await workspace.ExecuteAsync(args, conn.Endpoint, conn.Token, ct);
                        outputs.Add(new(call.CallId, wsResult));
                        traced.Add(new("run_command", Str(args, "cmd"), Summarize(wsResult)));
                    }
                    else
                    {
                        outputs.Add(new(call.CallId, """{"error":"The sandbox could not be started for this run."}"""));
                        traced.Add(new("run_command", Str(args, "cmd"), "no sandbox"));
                    }
                    break;
```

- [ ] **Step 2 — `FoundryAgentRuntime` passes the provider.** In `RunRoundAsync`, the `RoundExecutor.ExecuteOneRoundAsync(...)` call currently ends with `_workspaceExecutor, req.WorkspaceEndpoint, req.WorkspaceToken, ct`. Change to `_workspaceExecutor, req.Workspace, ct`. In `RunTurnAsync` (compact path), the `AgentToolLoop` is constructed with workspace endpoint/token `null` — change that to pass a `null` provider (see Step 3).

- [ ] **Step 3 — `AgentToolLoop` plumbing.** Replace the `_workspaceEndpoint`/`_workspaceToken` fields + ctor params with a single `IWorkspaceProvider? _workspaceProvider` (ctor param `IWorkspaceProvider? workspaceProvider = null`). Its call to `RoundExecutor.ExecuteOneRoundAsync(... _workspace, _workspaceEndpoint, _workspaceToken, ct)` becomes `... _workspace, _workspaceProvider, ct`. Update the `FoundryAgentRuntime.RunTurnAsync` construction of `AgentToolLoop` accordingly (pass `workspaceProvider: null`).

- [ ] **Step 4 — `run_command` description (de-git-ify).** In `TectikaToolSchema.cs`, replace the `RunCommandTool` description string with:
```csharp
        "Run a bash shell command in your sandbox workspace at `/workspace`. " +
        "Returns stdout, stderr, and exit_code. The sandbox is created the first time you call this. " +
        "When a GitHub repo is connected to the board it is cloned into `/workspace` with git configured " +
        "(you can git commit / git push); otherwise `/workspace` is an empty sandbox with no git repo. " +
        "Use it to write and run code, run builds (dotnet build, npm install), execute tests, etc. " +
        "Prefer small focused commands; chain them with &&. For file edits prefer 'cat > path <<EOF ... EOF'.",
```

- [ ] **Step 5 — fix existing tests for the new signatures.** Build the tests and fix call sites: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`. Tests that call `RoundExecutor.ExecuteOneRoundAsync(...)` or construct `AgentToolLoop`/`RoundRequest` with the old workspace params (`RoundExecutorTests`, `AgentToolLoopTests`, `MockRunRoundTests`, and any `RoundRequest`/`RunRoundAsync` callers) must pass the new shape — for these tests pass `null` for the provider (no sandbox in unit tests). Do NOT weaken assertions; only adapt the workspace argument. Re-run until green.

- [ ] **Step 6 — commit:** `git add -A && git commit -m "feat(runtime): lazy workspace provider in the round executor"`

---

## Task 3: WorkspaceService — standalone (git-isolated) provisioning

**Files:** modify `src/workflows/Services/WorkspaceService.cs`; test `tests/TectikaAgents.Tests/WorkspaceEnvTests.cs` (new).

- [ ] **Step 1 — extract a testable env-var helper.** Add a `public static` method to `WorkspaceService` that builds the container env-var list, including the repo vars **only when a repo is connected**:
```csharp
    /// <summary>Env vars for the workspace container. Repo vars (REPO_URL/GIT_BRANCH/GIT_PAT) are
    /// included only when a repo is connected — the entrypoint provisions a bare, git-free /workspace
    /// otherwise.</summary>
    public static List<ContainerEnvironmentVariable> BuildEnv(
        GitHubRepoConnection? github, string branchName, string token, string? pat)
    {
        var env = new List<ContainerEnvironmentVariable>
        {
            new("EXECUTOR_TOKEN") { SecureValue = token },
        };
        if (github is not null)
        {
            env.Add(new("REPO_URL")   { Value = github.RepoUrl });
            env.Add(new("GIT_BRANCH") { Value = branchName });
            env.Add(new("GIT_PAT")    { SecureValue = pat });
        }
        return env;
    }
```

- [ ] **Step 2 — provision in both cases.** In `ProvisionAsync`: remove the `if (board.GitHub is null) return null;` early-out. Fetch the PAT only when `board.GitHub is not null` (`var pat = board.GitHub is null ? null : await _secrets.GetSecretAsync(board.GitHub.PatSecretName, ct);`). Replace the inline `EnvironmentVariables = { ... }` initializer on the container with assigning the result of `BuildEnv(board.GitHub, branchName, token, pat)` (e.g. set `workspaceContainer.EnvironmentVariables` items from `BuildEnv(...)`, or construct the container then add them). Update the `IWorkspaceService.ProvisionAsync` XML doc to say it provisions a sandbox in both cases (no longer "returns null when no GitHub").

- [ ] **Step 3 — test** `tests/TectikaAgents.Tests/WorkspaceEnvTests.cs`:
```csharp
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class WorkspaceEnvTests
{
    [Fact]
    public void Standalone_has_only_executor_token_no_repo_vars()
    {
        var env = WorkspaceService.BuildEnv(null, "agent/x", "tok", null);
        Assert.Single(env);
        Assert.Equal("EXECUTOR_TOKEN", env[0].Name);
    }

    [Fact]
    public void With_repo_includes_repo_vars()
    {
        var gh = new GitHubRepoConnection { RepoUrl = "https://github.com/o/r", Owner = "o", Repo = "r", PatSecretName = "s" };
        var env = WorkspaceService.BuildEnv(gh, "agent/x", "tok", "pat");
        Assert.Contains(env, e => e.Name == "REPO_URL");
        Assert.Contains(env, e => e.Name == "GIT_BRANCH");
        Assert.Contains(env, e => e.Name == "GIT_PAT");
        Assert.Contains(env, e => e.Name == "EXECUTOR_TOKEN");
    }
}
```
(Confirm `GitHubRepoConnection`'s exact property names by reading the model; adjust the initializer if needed.)

- [ ] **Step 4 — build + test:** `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` → green.
- [ ] **Step 5 — commit:** `git add -A && git commit -m "feat(workflows): standalone (repo-less) sandbox provisioning"`

---

## Task 4: RunAgentRoundActivity — lazy provider + round-0 prompt; run-doc patch

**Files:** modify `src/workflows/Activities/RunAgentRoundActivity.cs`, `src/workflows/Services/WorkflowCosmosService.cs`; test `tests/TectikaAgents.Tests/WorkspacePromptTests.cs` (new).

- [ ] **Step 1 — extend the run-doc patch.** In `WorkflowCosmosService.PatchRunWorkspaceAsync`, add `string? endpoint = null, string? token = null` params and set `run.WorkspaceEndpoint = endpoint; run.WorkspaceToken = token;` alongside `WorkspaceContainerName` before the replace. Also add a reader the cleanup uses:
```csharp
    public async Task<string?> GetRunWorkspaceContainerAsync(string runId, CancellationToken ct = default)
    {
        var q = new QueryDefinition("SELECT * FROM c WHERE c.id=@id").WithParameter("@id", runId);
        var iter = C("workflowRuns").GetItemQueryIterator<WorkflowRun>(q);
        while (iter.HasMoreResults)
            foreach (var r in await iter.ReadNextAsync(ct)) return r.WorkspaceContainerName;
        return null;
    }
```
(And expose a small read of the run's existing endpoint/token for reuse — add `GetRunAsync`-style read or reuse the query in the provider; simplest: a `GetRunWorkspaceAsync(runId)` returning `(container, endpoint, token)`.)

- [ ] **Step 2 — round-0 prompt helper (testable).** Add to `RunAgentRoundActivity` (or a small static class) a pure method:
```csharp
    public static string WorkspacePrompt(bool repoConnected) => repoConnected
        ? "\n\n## Sandbox\nYou have an on-demand sandbox terminal via `run_command`. On first use, the connected " +
          "GitHub repository is cloned to `/workspace` with git configured (you can `git commit`/`git push`)."
        : "\n\n## Sandbox\nYou have an on-demand sandbox terminal via `run_command` — an empty `/workspace` " +
          "(no git repo connected). Use it to write and run code.";
```
Replace the existing `if (!string.IsNullOrEmpty(input.WorkspaceEndpoint)) ... else ...` block (the `## Workspace` text, ~lines 86-95) with `userInput += WorkspacePrompt(board.GitHub is not null);`.

- [ ] **Step 3 — the lazy provider.** Add a private nested class in `RunAgentRoundActivity` implementing `IWorkspaceProvider`, constructed with what it needs (`WorkflowCosmosService`, `IWorkspaceService`, the `Board`, `runId`, and a cached field). `EnsureAsync`:
  1. if cached → return it;
  2. read the run's stored workspace (endpoint/token) — if present → cache + return;
  3. else `ProvisionAsync(board, branchName, runId)` (branchName e.g. `$"agent/{runId[..8]}"`), then `PatchRunWorkspaceAsync(runId, info.ContainerName, info.Endpoint, info.Token)`, cache `new WorkspaceConnection(info.Endpoint, info.Token)`, return. On provisioning exception → log + return null.

  Construct this provider in `Run` and pass it into the `RoundRequest` (replace the removed `WorkspaceEndpoint`/`WorkspaceToken` with `Workspace = provider`). The activity needs `IWorkspaceService` injected — add it to the constructor (DI already registers `IWorkspaceService`).

- [ ] **Step 4 — remove `input.WorkspaceEndpoint` usage** (now handled by the provider/prompt). Keep `RoundActivityInput`'s fields for now if still referenced by the orchestrator; they're removed in Task 5.

- [ ] **Step 5 — prompt test** `tests/TectikaAgents.Tests/WorkspacePromptTests.cs`:
```csharp
using TectikaAgents.Workflows.Activities;
using Xunit;

namespace TectikaAgents.Tests;

public class WorkspacePromptTests
{
    [Fact] public void Repo_prompt_mentions_git() =>
        Assert.Contains("git", RunAgentRoundActivity.WorkspacePrompt(true));
    [Fact] public void Standalone_prompt_says_no_git_repo() =>
        Assert.Contains("no git repo", RunAgentRoundActivity.WorkspacePrompt(false));
    [Fact] public void Both_offer_run_command() {
        Assert.Contains("run_command", RunAgentRoundActivity.WorkspacePrompt(true));
        Assert.Contains("run_command", RunAgentRoundActivity.WorkspacePrompt(false));
    }
}
```

- [ ] **Step 6 — build workflows + test:** `dotnet build $(find src/workflows -name '*.csproj' | head -1) && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` → green.
- [ ] **Step 7 — commit:** `git add -A && git commit -m "feat(workflows): lazy on-demand sandbox provider + on-demand prompt"`

---

## Task 5: Orchestrator — drop eager provision, lazy cleanup

**Files:** modify `src/workflows/Orchestrators/SteerableAgentOrchestrator.cs`, `src/workflows/Activities/DestroyWorkspaceActivity.cs`, `src/workflows/Activities/RunAgentRoundActivity.cs` (RoundActivityInput); remove `src/workflows/Activities/ProvisionWorkspaceActivity.cs`.

- [ ] **Step 1 — drop eager provisioning.** In `SteerableAgentOrchestrator.Run`, remove the `ProvisionWorkspaceActivity` call + the `workspace` variable + passing `WorkspaceEndpoint/Token` to `RoundActivityInput` (the round activity now provisions lazily). `DurableRoundDriver` no longer needs the `workspace` field.

- [ ] **Step 2 — cleanup by runId.** Change `DestroyWorkspaceActivity` to accept the `runId`, read the container via `WorkflowCosmosService.GetRunWorkspaceContainerAsync(runId)`, and destroy only if non-null (no-op otherwise). In the orchestrator `finally`, always call `DestroyWorkspaceActivity` with `_in.RunId`.

- [ ] **Step 3 — RoundActivityInput cleanup.** Remove the `WorkspaceEndpoint`/`WorkspaceToken` params from `RoundActivityInput` and the orchestrator's construction of it.

- [ ] **Step 4 — retire `ProvisionWorkspaceActivity.cs`** (delete the file; confirm no remaining references via grep).

- [ ] **Step 5 — build workflows + full test suite:** `dotnet build $(find src/workflows -name '*.csproj' | head -1) && dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` → green.
- [ ] **Step 6 — commit:** `git add -A && git commit -m "feat(workflows): no eager provision; destroy sandbox by runId at run end"`

---

## Task 6: Executor image — git-isolated entrypoint

**Files:** modify `docker/workspace-executor/entrypoint.sh`, `docker/workspace-executor/executor.py`, `docker/workspace-executor/Dockerfile`.

- [ ] **Step 1 — conditional git in `entrypoint.sh`.** Replace it with:
```bash
#!/bin/bash
# Workspace container entrypoint.
#  - With REPO_URL set: configure git with the PAT, clone, check out GIT_BRANCH.
#  - Without REPO_URL: a bare, git-isolated /workspace (no clone, no git init).
# Then start the HTTP executor.
set -euo pipefail

mkdir -p /workspace

if [ -n "${REPO_URL:-}" ]; then
    echo "[entrypoint] repo=$REPO_URL branch=${GIT_BRANCH:-}"
    git config --global credential.helper store
    printf "https://x-access-token:%s@github.com\n" "${GIT_PAT:-}" > /root/.git-credentials
    git config --global user.email "agent@tectika.com"
    git config --global user.name "Tectika Agent"
    git clone "$REPO_URL" /workspace
    cd /workspace
    if git ls-remote --heads origin "${GIT_BRANCH:-}" | grep -q "${GIT_BRANCH:-}"; then
        git checkout "$GIT_BRANCH"
    else
        git checkout -b "$GIT_BRANCH"
    fi
    echo "[entrypoint] ready on branch $(git branch --show-current)"
else
    cd /workspace
    echo "[entrypoint] standalone sandbox (no repo) at /workspace"
fi

exec python3 /executor.py
```

- [ ] **Step 2 — de-git-ify comments.** In `executor.py`, change the docstring line "All commands run with cwd=/workspace (the cloned repo)." → "All commands run with cwd=/workspace (the sandbox; the repo when one is connected).". In `Dockerfile`, change the header comment "Entry: entrypoint.sh → clones repo → starts executor.py" → "Entry: entrypoint.sh → (clones repo if REPO_URL set) → starts executor.py".

- [ ] **Step 3 — sanity-check the script** parses: `bash -n docker/workspace-executor/entrypoint.sh` (exit 0). Optionally `REPO_URL= bash -c 'set -euo pipefail; : "${REPO_URL:-}"; echo ok'` to confirm the unset-guard pattern.

- [ ] **Step 4 — commit:** `git add -A && git commit -m "feat(docker): git-isolated standalone sandbox entrypoint"`

---

## Task 7: Full build + smoke

- [ ] **Step 1 — backend:** `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` → all pass (incl. new env + prompt tests).
- [ ] **Step 2 — full solution build** of api + workflows + agentruntime to catch ripple breaks.
- [ ] **Step 3 — manual smoke (after deploy: rebuild & push the `agent-workspace` ACR image, deploy workflows):**
  1. Agent on a **repo-less** board: chat "what's in your workspace / run `ls`" → first `run_command` provisions a bare `/workspace`; the agent does **not** claim git is ready.
  2. Agent on a **repo-connected** board: `run_command` → repo cloned, git works.
  3. A run that never calls `run_command` → no ACI created (check: no `tws-…` container); destroy is a no-op.

## Notes
- **Deploy:** rebuild + push the `agent-workspace` image to ACR (`docker/workspace-executor`), then deploy **workflows**. No API/web change.
- **Isolation:** all work happens in the `feat/sandbox-lazy-standalone` worktree; the other session is unaffected.
