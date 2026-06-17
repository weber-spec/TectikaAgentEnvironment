# Code Viewer — Design (Spec 2 of the Repo-Viewer initiative)

- **Date:** 2026-06-17
- **Status:** Draft for review
- **Author:** brainstormed with Claude
- **Scope of this spec:** A **board-level Repo view** that lets a user browse the connected GitHub repository the agents produce — file tree, syntax-highlighted file viewer, branch switcher, commit History, and Pull Requests — backed by a new typed **GitHub read service** in the API. Read-only.

---

## 1. Context & vision

TectikaAgentEnvironment automates whole projects with agents. The originating feature request: let the user **see and test the repository the agents built, from inside the interface** (the Lovable / v0 / bolt.new shape — Preview ↔ Code ↔ Console). Spec 1 (merged) generalized the task **handoff model** (`Artifact = summary + typed outputs[]`, where an output is inline or an external reference). This spec delivers the first piece of the "see the code" half: a **board-level repo browser**.

### Roadmap (this spec is step 2)

1. **Spec 1 (done): the handoff model** — `summary` + `outputs[]` (inline | external), Document kind wired end-to-end.
2. **Spec 2 (this doc): board-level Code viewer** — a GitHub read service + a "Repo" view on the board (Code / History / Pull Requests). Pure GitHub-read; no agent-side changes. Universal "see the code" value for any connected repo.
3. **Spec 3 (next): task-level Code deliverable** — agents declare a **Code output** (a git branch/PR ref via `declare_output`), the system enriches it (commit SHA, changed files) using this spec's read service, and the task's right-hand pane renders the diff + PR link. Requires the git-locator typing decision and `declare_output` extension. Also adds the **compare/diff** read op.
4. **Later phases:** live preview (build + run the app + shareable URL — the lazy on-demand sandbox that landed alongside Spec 1 is its foundation) and an interactive console.

### Why board-first

The board repo browser is self-contained (pure GitHub-read), needs no agent-side production changes, has no dependency on the unresolved git-locator typing, and de-risks the **shared read service** that Spec 3 also needs. It ships the "see the code" core fastest and works for every connected repo immediately — even repos where agents have not declared outputs.

---

## 2. Problem statement

Today the system can connect a board to a GitHub repo (`Board.GitHub`) and agents can read/write it via tools, but **the UI has zero visibility into the repository**. A user cannot see the files, browse history, or view pull requests without leaving the product for github.com. The API can technically reach GitHub (the Octokit tool executor and `ISecretProvider` are registered in its DI) but exposes no repo-browsing endpoints, and the existing `OctokitGitHubToolExecutor` is shaped for tool-name dispatch (read-file / list / create-branch / push / create-PR), missing the read operations a browser needs (list branches, tree, commits, PRs).

---

## 3. Scope

**In scope (Spec 2):**
- A typed **`IGitHubReadService`** + `OctokitGitHubReadService` with: repo metadata, list branches, list directory (tree at a ref), get file (at a ref), list commits, list pull requests, get pull request.
- A **`RepoController`** exposing board-scoped read endpoints.
- Short-TTL **caching** to respect GitHub rate limits.
- A board-level **Repo view** (frontend): pinned tab → `RepoView` with **Code / History / Pull Requests** sub-tabs + branch switcher; syntax-highlighted file viewer.
- Friendly **empty/error states** (no GitHub connected, empty repo, binary/large file, rate-limited, permission failure).
- Targeted refactor: extract the shared **PAT-resolve + `GitHubClient` construction** from `OctokitGitHubToolExecutor` so the read service and the tool executor share one code path.

**Out of scope (later specs):**
- **Diffs / compare** between refs (file-level diff rendering) — **Spec 3** (task-level Code deliverable needs it too).
- **Code outputs** / agent-side production / `declare_output` extension / git-locator typing — **Spec 3**.
- **Editing / writing** from the viewer (read-only here).
- **Live preview / running the app**, interactive console — later phases.

---

## 4. Architecture

### 4.1 GitHub read service (`agentruntime`)

New `IGitHubReadService` next to `OctokitGitHubToolExecutor`, reusing Octokit + `ISecretProvider`. All methods take the board's `GitHubRepoConnection` (owner/repo/PatSecretName) and resolve the PAT per call.

```csharp
public interface IGitHubReadService
{
    Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection repo, CancellationToken ct);
    Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection repo, CancellationToken ct);
    Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct);
    Task<FileContent> GetFileAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct);
    Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection repo, string @ref, string? path, int page, CancellationToken ct);
    Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection repo, string state, CancellationToken ct);
    Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection repo, int number, CancellationToken ct);
}
```

Result DTOs (immutable records):
- `RepoMeta(string DefaultBranch, string? Description, bool Private)`
- `BranchInfo(string Name, string CommitSha)`
- `TreeEntry(string Name, string Path, string Type /* "file"|"dir" */, long Size)`
- `FileContent(string Path, string Sha, long Size, bool IsBinary, string? Text /* null when binary */)`
- `CommitInfo(string Sha, string Message, string Author, DateTimeOffset Date, string Url)`
- `PullRequestInfo(int Number, string Title, string State, string Author, string Head, string Base, string Url, DateTimeOffset CreatedAt)`

**Binary detection:** `FileContent.IsBinary` is true when the blob content contains a NUL byte or exceeds a size threshold (e.g. 1 MB); `Text` is null in that case so the UI shows a "view on GitHub" fallback rather than garbage.

**Shared client helper:** extract a `GitHubClientFactory` (PAT resolve via `ISecretProvider` + `new GitHubClient(...)`) used by both `OctokitGitHubReadService` and the existing `OctokitGitHubToolExecutor` (which currently inlines this).

### 4.2 API (`RepoController`)

`[ApiController] [Authorize] [Route("api/boards/{boardId}/repo")]`, board-scoped, tenant via the existing `tid` claim (mirroring other controllers):

| Endpoint | Returns |
|---|---|
| `GET meta` | `RepoMeta` |
| `GET branches` | `BranchInfo[]` |
| `GET tree?ref=&path=` | `TreeEntry[]` |
| `GET file?ref=&path=` | `FileContent` |
| `GET commits?ref=&path=&page=` | `CommitInfo[]` |
| `GET pulls?state=` | `PullRequestInfo[]` |
| `GET pulls/{number}` | `PullRequestInfo` |

Each loads the board; if `board.GitHub is null`, returns a typed **`409 GitHubNotConnected`** body the UI renders as the connect prompt (not a generic error). Otherwise calls the read service. `ref` defaults to the repo's default branch when omitted; `path` defaults to repo root; `state` defaults to `open`.

### 4.3 Caching

A short-TTL `IMemoryCache` layer (≈30–60 s) keyed by `boardId + endpoint + args`. Tree/file/branch/commit/PR responses are cached; this keeps interactive browsing under GitHub's rate limits without staleness that matters for a viewer. (Cache lives in the API process.)

### 4.4 Frontend (`tectika-board`)

- **Placement:** a pinned **Repo** entry appended to the view-tabs row (`ViewTabs.tsx`), visually separated from the task views, that switches the board body to a `RepoView` component. `RepoView` is independent of the view/filter/sort machinery (it renders a repository, not a task collection).
- **`RepoView`** holds the active branch + active sub-tab (`Code` | `History` | `Pull Requests`) and a branch switcher (from `GET branches`, default from `GET meta`).
  - **Code:** a `FileTree` (lazy-loads directories via `GET tree`) + a `FileViewer` (`GET file`) with **Shiki** syntax highlighting by file extension (fallback: `highlight.js` if Shiki's async/WASM setup proves heavy in the Next.js build — decided at implementation time; Shiki preferred for fidelity). Binary/large files show the "view on GitHub" fallback.
  - **History:** commit list from `GET commits` (sha · message · author · relative date · link), with pagination.
  - **Pull Requests:** list from `GET pulls` (title · state · head→base · author · link), open/closed filter.
- **API client:** add an `api.repo` group in `lib/api.ts` mirroring the existing fetch-wrapper style; types mirror the C# DTOs in `lib/types.ts`.

---

## 5. Data flow

`RepoView` → `api.repo.{meta,branches,tree,file,commits,pulls}` → `RepoController` → `IMemoryCache` (hit returns immediately) → `IGitHubReadService` → Octokit → GitHub. The board's PAT is resolved per request via `ISecretProvider`; nothing is read from the ephemeral workspace (GitHub is the durable source of truth, consistent with Spec 1's principle).

---

## 6. Error handling / edge cases

- **No GitHub connected** (`board.GitHub is null`): API returns `409 GitHubNotConnected`; `RepoView` renders a "Connect a GitHub repo" prompt wired to the existing `GitHubConnectModal`.
- **Empty repo / empty directory:** read service returns empty lists; UI shows an empty state.
- **Binary or very large file:** `FileContent.IsBinary == true`, `Text == null`; viewer shows file name + size + "view on GitHub" link.
- **GitHub rate limit / transient error:** API surfaces a typed error; UI shows a friendly message with a retry action.
- **Permission / bad PAT (401/403):** typed error → "check the connected GitHub token" message.
- **Path/ref not found (404):** typed error → "not found on this branch" empty state.

---

## 7. Testing

- **Backend unit (`OctokitGitHubReadService`):** behind an Octokit-client abstraction (so tests don't hit the network) — branches, directory listing, file (text + binary detection via NUL byte / size threshold), commits (with/without path), PR list + get. Each asserts the DTO mapping.
- **Backend contract (`RepoController`):** board-not-found → 404; `board.GitHub is null` → `409 GitHubNotConnected`; happy paths via a fake `IGitHubReadService`; `ref`/`path`/`state` defaulting.
- **Caching:** a second identical request within TTL does not call the read service (verified with a counting fake).
- **Frontend:** `RepoView` renders tree/file/history/PRs; each empty/error state (no-GitHub prompt, binary fallback, rate-limit message); `npm run build` + lint clean; `node --test` for pure helpers (binary detection mirror, path join/normalize, extension→language mapping).

---

## 8. Decisions made (during brainstorming)

- **Board-first**, before the task-level Code deliverable (self-contained, de-risks the shared read service).
- **Repo view v1 = Browse + History + PRs**; **diffs/compare deferred to Spec 3** (where the task-level diff needs the same op).
- **Typed `IGitHubReadService`**, not an extension of the tool-name-dispatch executor (cleaner for API consumption; shared client construction extracted).
- **Read live from GitHub** (durable), not the ephemeral workspace.
- **Pinned Repo tab** in the view row (its own component), not a separate route.
- **Shiki** for syntax highlighting (fallback `highlight.js`).

---

## 9. Open questions

- Exact GitHub rate-limit headroom / cache TTL tuning — start at ~60 s, adjust if browsing feels stale or limits bite. Settled in the plan.
- Pagination page size for commits/PRs — default 30 (GitHub default); revisit if needed.

---

## 10. Carried forward to Spec 3 (task-level Code deliverable)

- **`ExternalRef.Locator` typing.** Still `Dictionary<string, object?>` (Spec 1 §12 carry-over). Spec 3 first *produces* git outputs, so it must pick a concrete approach (typed per-provider locator vs. `JsonElement`/`JsonNode`) before reading stored locators.
- **`declare_output` extension** to declare a `Code`/`external` output (provider=github, branch/PR ref), with system-side **enrichment** (commit SHA, changed files) via this spec's read service.
- **Compare/diff read op** (`CompareAsync(base, head)`) added to `IGitHubReadService`, plus the task right-pane diff renderer that replaces the current `OutputView` "coming soon" placeholder for `Code` outputs.
- **Branch persistence** — the run's branch is currently computed `agent/{runId[:8]}` and not stored; Spec 3 should persist it (on `WorkflowRun` or via the declared Code output's locator) so a task's code is resolvable after the run.
