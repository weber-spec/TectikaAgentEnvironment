# Task-level Code Deliverable — Design (Spec 3 of the Repo-Viewer initiative)

- **Date:** 2026-06-17
- **Status:** Draft for review
- **Scope of this spec:** Make a task's code change a first-class, viewable deliverable. When a run produces commits on its branch, the system **automatically** attaches an enriched **Code** output (git refs + changed-file counts + PR) to the task's artifact; the task pane shows a compact **Code card**, and "Open diff" opens the wide board **Repo → Changes** surface (base…branch diff). Read-only viewing.

---

## 1. Context & roadmap

The repo-viewer initiative: let users see and test the project agents build, in-app.

1. **Spec 1 (done):** generalized handoff model — `Artifact = summary + typed outputs[]` (inline | external). Document kind wired.
2. **Spec 2 (done):** board-level **Code viewer** — `IGitHubReadService` + board **Repo** view (Code / History / Pull Requests + branch switcher).
3. **Spec 3 (this doc):** task-level **Code deliverable** — the `Code` output kind, produced automatically per run and rendered as a compact card that opens a full **diff** in the Repo surface.
4. **Later phase:** **live preview** (build & run the app + shareable URL — on the lazy on-demand sandbox foundation) and an interactive console.

This spec completes the "see what *this* task changed" half: Spec 1 made outputs typed, Spec 2 built the read service + wide repo surface; Spec 3 produces `Code` outputs and renders their diffs, reusing both.

---

## 2. Problem statement

A task that writes code commits to its run branch (`agent/{runId[:8]}`) in an **ephemeral** sandbox. Today: nothing surfaces that change — the `OutputView` shows a "Code output rendering coming soon" placeholder, the branch may not even survive the sandbox teardown, and there is no diff view tied to a task. We need the code change to become a durable, viewable deliverable on the task's artifact, without burdening the agent and without cramming a file-browser into the narrow task panel.

---

## 3. Decisions (from brainstorming)

- **Automatic Code output** (not explicit declaration): at finalization, if the run branch has changes vs base, the system attaches an enriched Code output. `declare_output` stays Document-focused — **no `declare_output` extension needed**.
- **Branch durability:** the run branch is **pushed to origin at finalization** (before teardown), so the change survives and is enrichable. (Plugs a latent gap where unpushed agent work was lost.)
- **Task pane = compact Code card; full diff = board Repo "Changes" mode** (Option A) — the ~520px artifact pane is too narrow for a file-tree + diff, so the heavy diff reuses the wide Spec 2 Repo surface.
- **Locator typing:** `ExternalRef.Locator` changes from `Dictionary<string, object?>` to **`Dictionary<string, string>`** (resolves the Spec 1 §12 `JsonElement` round-trip concern). The diff itself is **fetched live**, never stored.
- **Diff source:** a new `CompareAsync(base, head)` read op + a `/repo/compare` API endpoint; the frontend fetches the diff on demand (consistent with how the repo browser fetches files live).

---

## 4. Scope

**In scope (Spec 3):**
- `CompareAsync(repo, base, head)` on `IGitHubReadService` (+ Octokit impl) → changed files `{ path, status, additions, deletions, patch }`; cached by the existing decorator.
- `ExternalRef.Locator` → `Dictionary<string,string>` (model change; nothing produces External yet, so non-breaking).
- `WorkflowRun.BranchName` (+ optional `PullRequestNumber`).
- **Finalization branch-push** (run branch → origin, before teardown) + **automatic Code-output enrichment** in `RunAgentRoundActivity` via `IGitHubReadService` (registered in the workflows DI).
- Framework directive update: instruct agents to commit/push their branch and open a PR when appropriate.
- `GET /api/boards/{boardId}/repo/compare?base=&head=` on `RepoController`.
- Frontend: `api.repo.compare` + types; the task-pane **Code card** (replaces the `OutputView` Code placeholder); a `board-context` `openRepoChanges(head)` signal; a **Changes** sub-tab in `RepoView` with a per-file diff renderer (unified-diff hunk parser).

**Out of scope (later):**
- **Live preview / running the app**, interactive console — next phase.
- **Editing / committing** from the UI (read-only).
- Inline review comments / approving PRs from the UI.
- Syntax-highlighting *within* diff hunks (green/red line coloring only for v1; Shiki-on-diff is a nice-to-have).

---

## 5. Architecture

### 5.1 Read service: `CompareAsync`
Add to `IGitHubReadService`:
```csharp
Task<CompareResult> CompareAsync(GitHubRepoConnection repo, string @base, string head, CancellationToken ct);
```
DTOs:
```csharp
public sealed record CompareResult(int FilesChanged, int Additions, int Deletions, IReadOnlyList<DiffFile> Files);
public sealed record DiffFile(string Path, string Status, int Additions, int Deletions, bool IsBinary, string? Patch);
```
Octokit: `client.Repository.Commit.Compare(owner, repo, base, head)` → `RepositoryCompareCommits` with `.Files[]` (`Filename`, `Status`, `Additions`, `Deletions`, `Patch`). A pure mapper converts to the DTOs; `IsBinary` = `Patch is null` (GitHub omits patches for binary/oversize). Cached by the existing `CachedGitHubReadService` (key includes base+head). `NotFoundException` → a typed "ref not found" error.

### 5.2 Model: locator typing + WorkflowRun
- `ExternalRef.Locator` → `Dictionary<string, string>`. Update the frontend `ExternalRef` type to `Record<string, string>`. Update any existing `Output`/`OutputAccumulator` tests referencing the old type (none produce External yet).
- `WorkflowRun` gains `[JsonPropertyName("branchName")] string? BranchName` and `[JsonPropertyName("pullRequestNumber")] int? PullRequestNumber`, persisted at finalization.

### 5.3 Finalization: push + enrich (`RunAgentRoundActivity`, Final round)
1. **Push** the run branch to origin via the workspace executor (`git push origin HEAD:agent/{runId[:8]}`) while the sandbox is still alive — best-effort; log on failure.
2. Inject `IGitHubReadService` (register in workflows `Program.cs`).
3. `base` = repo default branch (`GetRepoMetadataAsync().DefaultBranch`); `head` = run branch.
4. `CompareAsync(base, head)`. If `FilesChanged > 0`:
   - Look up the PR for the head branch (`ListPullRequestsAsync` filtered to head) → `prNumber`/`prUrl` if present.
   - Build a Code `Output` (`Kind = Code`, `External` provider `github`, locator `{ owner, repo, branch, base, headSha, prNumber?, prUrl?, filesChanged, additions, deletions }`); append to the artifact's `Outputs`.
   - Persist `WorkflowRun.BranchName` (+ `PullRequestNumber`).
5. If compare fails / 0 files / branch absent → no Code output (Document outputs still attach; the run is never blocked).

A pure helper `BuildCodeOutput(refs, compareResult, pr)` does the Output construction (unit-tested).

### 5.4 API: compare endpoint
`GET /api/boards/{boardId}/repo/compare?base=&head=` on `RepoController` → `CompareResult`; same board-load + `409 GitHubNotConnected` + tenant scoping as the other repo endpoints; `base` defaults to the repo default branch when omitted.

### 5.5 Frontend
- **Code card** (`OutputView`, `kind === 'Code'`): reads `external.locator`; renders branch · base · ± · file list (first few + "N more") · PR link · **"Open diff"**. Replaces the current placeholder.
- **`openRepoChanges(head)`** in `board-context`: sets state consumed by `BoardView` (open Repo tab) and `RepoView` (Changes mode + head branch). The Code card calls it (and closes the task panel).
- **Repo "Changes" sub-tab** (`RepoView`, alongside Code/History/Pull Requests): `api.repo.compare(base, head)` → changed-files list + per-file diff in the wide surface; a branch picker for `head` (deep-linked from the card), `base` = default branch.
- **Diff renderer:** a pure `parseUnifiedDiff(patch)` → hunks/lines (`node --test`); render added/removed/context lines with green/red coloring; binary/large files show a "view on GitHub" link.
- `api.repo.compare` + `CompareResult`/`DiffFile` types mirror the C# DTOs.

---

## 6. Data flow

*Produce:* agent commits → finalization pushes branch → `RunAgentRoundActivity` → `CompareAsync` + PR lookup → `BuildCodeOutput` → artifact `Outputs` (+ `WorkflowRun.BranchName`).
*Consume:* task pane `OutputView` Code card (reads locator) → "Open diff" → `board-context.openRepoChanges(head)` → `BoardView` shows Repo, `RepoView` opens Changes(head) → `api.repo.compare` → `RepoController.Compare` → `CompareAsync` → diff rendered. GitHub is the source of truth throughout; the artifact stores only refs.

---

## 7. Error handling / edges

- Branch not pushed / 0 changes / compare throws at finalization → no Code output; run completes normally.
- Finalization push fails → logged; enrich best-effort against whatever is on GitHub, else no Code output.
- No PR for the head branch → card omits the PR link; diff still works.
- Compare endpoint: base/head 404 → typed error → Changes tab empty/error state. No GitHub connected → `409 GitHubNotConnected`.
- Binary/oversize file in a diff (`Patch is null`) → per-file "binary — view on GitHub"; very large patches truncated with a GitHub link.

---

## 8. Testing

- **Backend:** pure Octokit-compare → `CompareResult` mapper; pure `BuildCodeOutput` helper (refs + compare + PR → Output with correct locator); `RepoController.Compare` contract (404/409/happy via a fake `IGitHubReadService`); the locator type change updates existing `Output`/`OutputAccumulator`/serialization tests; cache key includes base+head (counting-fake test). Finalization push + enrichment wiring verified by build + the pure helpers.
- **Frontend:** pure `parseUnifiedDiff` (`node --test`); Code-card render from a locator fixture; `compare` client; Changes sub-tab + diff renderer; `openRepoChanges` wiring; `tsc`/`lint`/`build` + a manual render check (board with a connected repo + an agent run that pushed a branch).

---

## 9. Decomposition (two plans, backend first)

- **Plan 3A (backend):** `CompareAsync` + DTOs + Octokit mapper; `ExternalRef.Locator` → `Dictionary<string,string>`; `WorkflowRun` branch/PR fields; finalization push + automatic enrichment (`BuildCodeOutput`) + workflows DI; `/repo/compare` endpoint; the push/PR framework directive.
- **Plan 3B (frontend):** `compare` client + types; task-pane Code card; `board-context.openRepoChanges`; Repo "Changes" sub-tab + `parseUnifiedDiff` + diff renderer.

Each produces working, independently-testable software; 3A lands the data + endpoint, 3B the UI.

---

## 10. Open questions / forward notes

- **Default base for compare** is the repo default branch; if a task's PR targets a different base, v1 still diffs against default. Refine to use the PR's base when a PR exists (small follow-up).
- **Finalization push mechanics** depend on the workspace still being alive at the Final round (it is, before teardown). If a run ends without a workspace (no GitHub board), there's simply no Code output. Settled in the plan.
- **Live preview (next phase):** the Code output's locator already carries the branch/PR; a future preview can build & run that ref and add a `previewUrl` to the same `ExternalRef`.

### Follow-ups after 3B (surfaced during final review — minor, not blocking)

- **Code card file list:** §5.5 envisioned the card showing the first few changed files. The locator stores only counts (`filesChanged`/`additions`/`deletions`), not filenames, so the card shows counts and defers the file list to the Changes tab. To list files on the card, either add the top filenames to the locator at enrichment time, or have the card lazily fetch `compare`. Acceptable as-is for v1.
- **Binary "view on GitHub" link:** §7 calls for a binary/oversize file in the diff to link to GitHub; the `DiffView` currently shows static "Binary file — not shown." Threading `owner`/`repo` into `ChangesTab`/`DiffView` (or building the blob URL from the locator) would let it render the link. Small follow-up.
