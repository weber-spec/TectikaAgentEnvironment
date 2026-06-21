# Task-level Code Deliverable — Plan 3A (Backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatically attach an enriched **Code** output to a task's artifact when its run produces commits — by pushing the run branch at finalization, comparing it against the default branch, and storing git refs — plus a `/repo/compare` API endpoint to serve the diff.

**Architecture:** Add `CompareAsync` to the GitHub read service (Octokit `Repository.Commit.Compare`). At the Final round, `RunAgentRoundActivity` pushes the run branch (best-effort, via the workspace executor), compares `default…branch`, and — if files changed — appends a Code `Output` (a pure `CodeOutputBuilder` builds it) whose `ExternalRef.Locator` (now `Dictionary<string,string>`) holds the refs. A `RepoController` endpoint exposes compare for the UI.

**Tech Stack:** C# / .NET 10, Octokit 13.0.1, xUnit (`tests/TectikaAgents.Tests`), Azure Durable Functions.

**Builds on:** Spec 1 + 2 (merged). **Scope note:** Plan 3A of two for Spec 3 (`docs/superpowers/specs/2026-06-17-task-code-deliverable-design.md`). Plan 3B (frontend: compare client, Code card, Repo "Changes" tab, diff renderer) is the follow-on. Live preview is a later phase.

---

## File Structure

**Modify:**
- `src/agentruntime/GitHub/GitHubReadModels.cs` — `CompareResult`/`DiffFile` DTOs + `CompareAsync` on `IGitHubReadService`.
- `src/agentruntime/GitHub/OctokitGitHubReadService.cs` — Octokit `CompareAsync`.
- `src/agentruntime/GitHub/CachedGitHubReadService.cs` — cache `CompareAsync`.
- `src/core/TectikaAgents.Core/Models/Output.cs` — `ExternalRef.Locator` → `Dictionary<string,string>`.
- `src/core/TectikaAgents.Core/Models/WorkflowRun.cs` — `BranchName` + `PullRequestNumber`.
- `src/workflows/Services/WorkflowCosmosService.cs` — `PatchRunBranchAsync`.
- `src/workflows/Activities/RunAgentRoundActivity.cs` — inject `IGitHubReadService`; finalization push + Code-output enrichment.
- `src/workflows/Program.cs` — register `IGitHubReadService` (+ `AddMemoryCache`).
- `src/workflows/Services/ContextManager.cs` — push/PR directive.
- `src/api/TectikaAgents.Api/Controllers/RepoController.cs` — `compare` endpoint.

**Create:**
- `src/workflows/Services/CodeOutputBuilder.cs` — pure builder (refs + compare + PR → `Output`).
- `tests/TectikaAgents.Tests/CompareMappingTests.cs`, `tests/TectikaAgents.Tests/CodeOutputBuilderTests.cs`, `tests/TectikaAgents.Tests/RepoControllerCompareTests.cs`.

---

## Task 1: `CompareAsync` on the read service

**Files:**
- Modify: `GitHubReadModels.cs`, `OctokitGitHubReadService.cs`, `CachedGitHubReadService.cs`
- Test: `tests/TectikaAgents.Tests/CompareMappingTests.cs`

- [ ] **Step 1: Add DTOs + interface method.** In `src/agentruntime/GitHub/GitHubReadModels.cs`, add the DTOs (next to the others) and the interface method (in `IGitHubReadService`):

```csharp
public sealed record DiffFile(string Path, string Status, int Additions, int Deletions, bool IsBinary, string? Patch);
public sealed record CompareResult(string HeadSha, int FilesChanged, int Additions, int Deletions, IReadOnlyList<DiffFile> Files);
```
Add to the `IGitHubReadService` interface:
```csharp
    Task<CompareResult> CompareAsync(GitHubRepoConnection repo, string @base, string head, CancellationToken ct);
```

- [ ] **Step 2: Write the failing test** for the pure mapper. Create `tests/TectikaAgents.Tests/CompareMappingTests.cs`:

```csharp
using TectikaAgents.AgentRuntime.GitHub;
using Xunit;

public class CompareMappingTests
{
    [Fact]
    public void MapsFiles_SumsAdditionsDeletions_FlagsBinary()
    {
        var files = new[]
        {
            new GitHubReadMapping.RawDiffFile("a.ts", "modified", 10, 2, "@@ -1 +1 @@\n-x\n+y"),
            new GitHubReadMapping.RawDiffFile("img.png", "added", 0, 0, null), // binary: null patch
        };
        var result = GitHubReadMapping.MapCompare("sha123", files);

        Assert.Equal("sha123", result.HeadSha);
        Assert.Equal(2, result.FilesChanged);
        Assert.Equal(10, result.Additions);
        Assert.Equal(2, result.Deletions);
        Assert.False(result.Files[0].IsBinary);
        Assert.Equal("@@ -1 +1 @@\n-x\n+y", result.Files[0].Patch);
        Assert.True(result.Files[1].IsBinary);
        Assert.Null(result.Files[1].Patch);
    }
}
```

- [ ] **Step 3: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~CompareMappingTests` → build error (`MapCompare`/`RawDiffFile` missing).

- [ ] **Step 4: Add the pure mapper.** In `src/agentruntime/GitHub/GitHubReadMapping.cs`, add (inside the `GitHubReadMapping` static class):

```csharp
    /// <summary>Provider-agnostic shape of one changed file, so the compare mapper is unit-testable
    /// without Octokit types.</summary>
    public sealed record RawDiffFile(string Filename, string Status, int Additions, int Deletions, string? Patch);

    public static CompareResult MapCompare(string headSha, IReadOnlyList<RawDiffFile> files)
    {
        var mapped = files
            .Select(f => new DiffFile(f.Filename, f.Status, f.Additions, f.Deletions, f.Patch is null, f.Patch))
            .ToList();
        return new CompareResult(headSha, mapped.Count, mapped.Sum(f => f.Additions), mapped.Sum(f => f.Deletions), mapped);
    }
```

- [ ] **Step 5: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~CompareMappingTests` → 1 pass.

- [ ] **Step 6: Implement `CompareAsync` in Octokit service.** In `src/agentruntime/GitHub/OctokitGitHubReadService.cs`, add the method (mirrors the existing methods' style):

```csharp
    public async Task<CompareResult> CompareAsync(GitHubRepoConnection repo, string @base, string head, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var cmp = await c.Repository.Commit.Compare(repo.Owner, repo.Repo, @base, head);
        var raw = (cmp.Files ?? new List<Octokit.GitHubCommitFile>())
            .Select(f => new GitHubReadMapping.RawDiffFile(f.Filename, f.Status, f.Additions, f.Deletions, f.Patch))
            .ToList();
        var headSha = cmp.Commits is { Count: > 0 } ? cmp.Commits[^1].Sha : head;
        return GitHubReadMapping.MapCompare(headSha, raw);
    }
```
(If a member name differs in Octokit 13.0.1 — e.g. `GitHubCommitFile.Patch`/`Additions` — adjust to the compiler's guidance; these mirror Octokit's documented `RepositoryCommitFile` shape.)

- [ ] **Step 7: Cache it.** In `src/agentruntime/GitHub/CachedGitHubReadService.cs`, add:

```csharp
    public Task<CompareResult> CompareAsync(GitHubRepoConnection repo, string @base, string head, CancellationToken ct) =>
        Cached(K(repo, "compare", @base, head), () => _inner.CompareAsync(repo, @base, head, ct));
```

- [ ] **Step 8: Build + suite.** `dotnet build src/agentruntime` (0 errors); `dotnet test tests/TectikaAgents.Tests` (all pass).

- [ ] **Step 9: Commit:**
```bash
git add src/agentruntime/GitHub/GitHubReadModels.cs src/agentruntime/GitHub/GitHubReadMapping.cs src/agentruntime/GitHub/OctokitGitHubReadService.cs src/agentruntime/GitHub/CachedGitHubReadService.cs tests/TectikaAgents.Tests/CompareMappingTests.cs
git commit -m "feat(agentruntime): add CompareAsync (base...head diff) to GitHub read service"
```

---

## Task 2: `ExternalRef.Locator` → `Dictionary<string,string>`

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/Output.cs`
- Test: existing `tests/TectikaAgents.Tests/OutputTests.cs` (verify still green)

- [ ] **Step 1: Change the type.** In `src/core/TectikaAgents.Core/Models/Output.cs`, change the `ExternalRef.Locator` property from:
```csharp
    [JsonPropertyName("locator")]
    public Dictionary<string, object?> Locator { get; set; } = new();
```
to:
```csharp
    [JsonPropertyName("locator")]
    public Dictionary<string, string> Locator { get; set; } = new();
```

- [ ] **Step 2: Build + suite.** `dotnet build src/core/TectikaAgents.Core`; `dotnet test tests/TectikaAgents.Tests`. Expected: all pass. (Nothing produces `External` yet; `OutputTests` constructs `ExternalRef` without a locator, so it still compiles. If any test references `Dictionary<string,object?>` for the locator, update it to `Dictionary<string,string>`.)

- [ ] **Step 3: Commit:**
```bash
git add src/core/TectikaAgents.Core/Models/Output.cs
git commit -m "refactor(core): ExternalRef.Locator -> Dictionary<string,string> (clean JSON round-trip)"
```

---

## Task 3: `WorkflowRun` branch/PR fields + Cosmos patch

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/WorkflowRun.cs`, `src/workflows/Services/WorkflowCosmosService.cs`

Mechanical (model field + patch); verified by build + Task 5's suite.

- [ ] **Step 1: Add the fields.** In `src/core/TectikaAgents.Core/Models/WorkflowRun.cs`, immediately after the `workspaceToken` property, add:
```csharp
    [JsonPropertyName("branchName")]
    public string? BranchName { get; set; }

    [JsonPropertyName("pullRequestNumber")]
    public int? PullRequestNumber { get; set; }
```

- [ ] **Step 2: Add the patch method.** In `src/workflows/Services/WorkflowCosmosService.cs`, near `PatchRunWorkspaceAsync` (find it), add:
```csharp
    public async Task PatchRunBranchAsync(string runId, string branchName, int? pullRequestNumber, CancellationToken ct = default)
    {
        var patchOps = new List<PatchOperation>
        {
            PatchOperation.Set("/branchName", branchName),
            PatchOperation.Set("/pullRequestNumber", pullRequestNumber),
        };
        await C("runs").PatchItemAsync<WorkflowRun>(runId, new PartitionKey(runId), patchOps, cancellationToken: ct);
    }
```
(Match the partition key + container name that `PatchRunWorkspaceAsync` uses — read it and mirror exactly; the `"runs"` container name and `PartitionKey(runId)` shown here must match that method.)

- [ ] **Step 3: Build.** `dotnet build src/workflows` → 0 errors.

- [ ] **Step 4: Commit:**
```bash
git add src/core/TectikaAgents.Core/Models/WorkflowRun.cs src/workflows/Services/WorkflowCosmosService.cs
git commit -m "feat: persist run branch + PR number on WorkflowRun"
```

---

## Task 4: `CodeOutputBuilder` (pure)

**Files:**
- Create: `src/workflows/Services/CodeOutputBuilder.cs`
- Test: `tests/TectikaAgents.Tests/CodeOutputBuilderTests.cs`

- [ ] **Step 1: Write the failing test.** Create `tests/TectikaAgents.Tests/CodeOutputBuilderTests.cs`:

```csharp
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

public class CodeOutputBuilderTests
{
    private static GitHubRepoConnection Repo() => new() { Owner = "acme", Repo = "shop", PatSecretName = "s" };
    private static CompareResult Cmp() => new("headsha", 3, 60, 8, new[]
    {
        new DiffFile("Cart.tsx", "added", 42, 0, false, "@@"),
    });

    [Fact]
    public void Build_SetsCodeKind_ExternalGithub_AndLocatorRefs()
    {
        var pr = new PullRequestInfo(12, "Checkout", "open", "agent", "agent/abc", "main", "https://gh/pr/12", System.DateTimeOffset.UtcNow);
        var o = CodeOutputBuilder.Build(Repo(), "main", "agent/abc", Cmp(), pr);

        Assert.Equal(OutputKind.Code, o.Kind);
        Assert.NotNull(o.External);
        Assert.Null(o.Inline);
        Assert.True(o.IsValid());
        Assert.Equal("github", o.External!.Provider);
        var loc = o.External.Locator;
        Assert.Equal("acme", loc["owner"]);
        Assert.Equal("shop", loc["repo"]);
        Assert.Equal("agent/abc", loc["branch"]);
        Assert.Equal("main", loc["base"]);
        Assert.Equal("headsha", loc["headSha"]);
        Assert.Equal("3", loc["filesChanged"]);
        Assert.Equal("60", loc["additions"]);
        Assert.Equal("8", loc["deletions"]);
        Assert.Equal("12", loc["prNumber"]);
        Assert.Equal("https://gh/pr/12", loc["prUrl"]);
        Assert.Equal("https://gh/pr/12", o.External.PreviewUrl);
    }

    [Fact]
    public void Build_WithoutPr_OmitsPrKeys()
    {
        var o = CodeOutputBuilder.Build(Repo(), "main", "agent/abc", Cmp(), null);
        Assert.False(o.External!.Locator.ContainsKey("prNumber"));
        Assert.False(o.External.Locator.ContainsKey("prUrl"));
        Assert.Null(o.External.PreviewUrl);
    }
}
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~CodeOutputBuilderTests` → build error.

- [ ] **Step 3: Implement.** Create `src/workflows/Services/CodeOutputBuilder.cs`:

```csharp
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Workflows.Services;

/// <summary>Builds the Code (external/github) Output for a task from its compare result + optional PR.
/// Pure — no I/O — so the locator contract is unit-tested.</summary>
public static class CodeOutputBuilder
{
    public static Output Build(GitHubRepoConnection repo, string baseBranch, string headBranch, CompareResult cmp, PullRequestInfo? pr)
    {
        var locator = new Dictionary<string, string>
        {
            ["owner"] = repo.Owner,
            ["repo"] = repo.Repo,
            ["branch"] = headBranch,
            ["base"] = baseBranch,
            ["headSha"] = cmp.HeadSha,
            ["filesChanged"] = cmp.FilesChanged.ToString(),
            ["additions"] = cmp.Additions.ToString(),
            ["deletions"] = cmp.Deletions.ToString(),
        };
        if (pr is not null)
        {
            locator["prNumber"] = pr.Number.ToString();
            locator["prUrl"] = pr.Url;
        }
        return new Output
        {
            Kind = OutputKind.Code,
            Label = pr is not null ? $"PR #{pr.Number}" : headBranch,
            External = new ExternalRef
            {
                Provider = "github",
                Locator = locator,
                PreviewUrl = pr?.Url,
            },
        };
    }
}
```

- [ ] **Step 4: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~CodeOutputBuilderTests` → 2 pass.

- [ ] **Step 5: Commit:**
```bash
git add src/workflows/Services/CodeOutputBuilder.cs tests/TectikaAgents.Tests/CodeOutputBuilderTests.cs
git commit -m "feat(workflows): CodeOutputBuilder — task code change -> Code output"
```

---

## Task 5: Finalization push + enrichment in `RunAgentRoundActivity` + DI

**Files:**
- Modify: `src/workflows/Activities/RunAgentRoundActivity.cs`, `src/workflows/Program.cs`

Integration (Durable + GitHub); verified by build + suite. The pure pieces (`MapCompare`, `CodeOutputBuilder`) are unit-tested in Tasks 1 & 4.

- [ ] **Step 1: Register the read service in workflows DI.** In `src/workflows/Program.cs`, immediately after `builder.Services.AddSingleton<IGitHubToolExecutor, OctokitGitHubToolExecutor>();`, add:
```csharp
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OctokitGitHubReadService>();
builder.Services.AddSingleton<IGitHubReadService>(sp =>
    new CachedGitHubReadService(
        sp.GetRequiredService<OctokitGitHubReadService>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
```
Add `using TectikaAgents.AgentRuntime.GitHub;` to `Program.cs` if not already present (the `OctokitGitHubToolExecutor` registration likely already pulls this namespace in — verify).

- [ ] **Step 2: Inject `IGitHubReadService` into the activity.** In `src/workflows/Activities/RunAgentRoundActivity.cs`, add a field + constructor param (mirroring the existing ones):
- Add field: `private readonly IGitHubReadService _ghRead;`
- Add ctor param `IGitHubReadService ghRead,` (e.g. after `IWorkspaceService workspace,`) and assign `_ghRead = ghRead;`
- Ensure `using TectikaAgents.AgentRuntime.GitHub;` is present at the top.

- [ ] **Step 3: Add the push + enrichment helpers.** Add these private methods to the `RunAgentRoundActivity` class (e.g. above `private static string Short`):

```csharp
    // Best-effort: push the run branch to origin so its commits are durable + enrichable.
    private async Task TryPushBranchAsync(string runId, CancellationToken ct)
    {
        try
        {
            var run = await _cosmos.GetRunByIdAsync(runId, ct);
            if (run?.WorkspaceEndpoint is null || run.WorkspaceToken is null) return; // no workspace was used
            var res = await _workspace.RunCommandAsync(run.WorkspaceEndpoint, run.WorkspaceToken,
                "cd /workspace && git push origin HEAD", 120, ct);
            _logger.LogInformation("[RunAgentRound] finalization push run={RunId} exit={Exit}", runId, res.ExitCode);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[RunAgentRound] finalization push failed run={RunId}", runId); }
    }

    // Best-effort: build the Code output for this run's branch (null if no repo / no changes / error).
    private async Task<Output?> TryBuildCodeOutputAsync(Board board, string runId, CancellationToken ct)
    {
        if (board.GitHub is null) return null;
        var head = $"agent/{runId[..Math.Min(8, runId.Length)]}";
        try
        {
            var meta = await _ghRead.GetRepoMetadataAsync(board.GitHub, ct);
            var cmp = await _ghRead.CompareAsync(board.GitHub, meta.DefaultBranch, head, ct);
            if (cmp.FilesChanged == 0) return null;
            var prs = await _ghRead.ListPullRequestsAsync(board.GitHub, "all", ct);
            var pr = prs.FirstOrDefault(p => p.Head == head);
            await _cosmos.PatchRunBranchAsync(runId, head, pr?.Number, ct);
            return CodeOutputBuilder.Build(board.GitHub, meta.DefaultBranch, head, cmp, pr);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[RunAgentRound] code-output enrichment failed run={RunId}", runId); return null; }
    }
```

- [ ] **Step 4: Wire into the Final-round artifact build.** In the `if (outcome.Kind == RoundKind.Final && outcome.Error is null)` block, replace the artifact-build region so it pushes, enriches, and appends the Code output:

Change:
```csharp
            var existing = await _cosmos.GetUpstreamArtifactsAsync([input.TaskId], ct);
            var nextVersion = (existing.MaxBy(a => a.Version)?.Version ?? 0) + 1;
            var artifact = new Artifact
            {
                TaskId = input.TaskId,
                RunId = input.RunId,
                TenantId = input.TenantId,
                Version = nextVersion,
                ContentType = ArtifactContentType.Markdown,
                Content = outcome.FinalText ?? "",        // back-compat: Content == summary; existing readers + EnsureHandoffShape still work
                Summary = outcome.FinalText ?? "",         // the agent's final message is the handoff summary
                Outputs = task.PendingOutputs.Where(o => o.IsValid()).ToList(),  // deliberately declared deliverables
                Origin = ArtifactOrigin.Agent,
                InternalLogs = [$"Agent: {role.DisplayName}", $"Round: {input.Round}", $"Completion: {outcome.CompletionId}"],
            };
            var saved = await _cosmos.CreateArtifactAsync(artifact, ct);
```
to:
```csharp
            await TryPushBranchAsync(input.RunId, ct);

            var existing = await _cosmos.GetUpstreamArtifactsAsync([input.TaskId], ct);
            var nextVersion = (existing.MaxBy(a => a.Version)?.Version ?? 0) + 1;

            var outputs = task.PendingOutputs.Where(o => o.IsValid()).ToList();  // deliberately declared deliverables
            var codeOutput = await TryBuildCodeOutputAsync(board, input.RunId, ct);  // automatic code deliverable
            if (codeOutput is not null) outputs.Add(codeOutput);

            var artifact = new Artifact
            {
                TaskId = input.TaskId,
                RunId = input.RunId,
                TenantId = input.TenantId,
                Version = nextVersion,
                ContentType = ArtifactContentType.Markdown,
                Content = outcome.FinalText ?? "",        // back-compat: Content == summary; existing readers + EnsureHandoffShape still work
                Summary = outcome.FinalText ?? "",         // the agent's final message is the handoff summary
                Outputs = outputs,
                Origin = ArtifactOrigin.Agent,
                InternalLogs = [$"Agent: {role.DisplayName}", $"Round: {input.Round}", $"Completion: {outcome.CompletionId}"],
            };
            var saved = await _cosmos.CreateArtifactAsync(artifact, ct);
```
(`CodeOutputBuilder`, `Output`, `Board` are all in scope already — the file uses `Artifact`/`Board`/`Output`. `System.Linq` is available.)

- [ ] **Step 5: Build + suite.** `dotnet build src/workflows` (0 errors); `dotnet test tests/TectikaAgents.Tests` (all pass — no regressions).

- [ ] **Step 6: Commit:**
```bash
git add src/workflows/Activities/RunAgentRoundActivity.cs src/workflows/Program.cs
git commit -m "feat(workflows): finalization branch push + automatic Code-output enrichment"
```

---

## Task 6: `/repo/compare` API endpoint

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/RepoController.cs`
- Test: `tests/TectikaAgents.Tests/RepoControllerCompareTests.cs`

- [ ] **Step 1: Write the failing test.** Create `tests/TectikaAgents.Tests/RepoControllerCompareTests.cs` (mirror the existing `RepoControllerTests` fakes — reuse the same `FakeCosmosForRepo` pattern; if that class is in the other test file and accessible, reuse it, otherwise define a minimal one the same way):

```csharp
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Controllers;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;
using Xunit;

public class RepoControllerCompareTests
{
    private sealed class FakeRead : IGitHubReadService
    {
        public Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult(new RepoMeta("main", null, false));
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult<IReadOnlyList<BranchInfo>>(System.Array.Empty<BranchInfo>());
        public Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult<IReadOnlyList<TreeEntry>>(System.Array.Empty<TreeEntry>());
        public Task<FileContent> GetFileAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult(new FileContent(p, "s", 0, false, ""));
        public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection r, string @ref, string? p, int page, CancellationToken ct) => Task.FromResult<IReadOnlyList<CommitInfo>>(System.Array.Empty<CommitInfo>());
        public Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection r, string s, CancellationToken ct) => Task.FromResult<IReadOnlyList<PullRequestInfo>>(System.Array.Empty<PullRequestInfo>());
        public Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection r, int n, CancellationToken ct) => Task.FromResult<PullRequestInfo?>(null);
        public Task<CompareResult> CompareAsync(GitHubRepoConnection r, string b, string h, CancellationToken ct) =>
            Task.FromResult(new CompareResult("sha", 1, 5, 1, new[] { new DiffFile("a.ts", "modified", 5, 1, false, "@@") }));
    }

    private static RepoController Make(Board? board)
    {
        var ctrl = new RepoController(new FakeCosmosForRepo(board), new FakeRead());
        var identity = new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim("tid", "default") }, "test");
        ctrl.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) } };
        return ctrl;
    }

    [Fact]
    public async Task Compare_NoGitHub_Returns409()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = null };
        Assert.IsType<ConflictObjectResult>(await Make(board).Compare("b1", "main", "agent/abc", default));
    }

    [Fact]
    public async Task Compare_Connected_ReturnsOk()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", PatSecretName = "s" } };
        var ok = Assert.IsType<OkObjectResult>(await Make(board).Compare("b1", "main", "agent/abc", default));
        var result = Assert.IsType<CompareResult>(ok.Value);
        Assert.Equal(1, result.FilesChanged);
    }
}
```
(NOTE: `FakeCosmosForRepo` already exists in `tests/TectikaAgents.Tests/RepoControllerTests.cs` from Spec 2. If it's `public`/accessible, reuse it; if it's file-private, hoist it to its own small `tests/TectikaAgents.Tests/FakeCosmosForRepo.cs` and have both test files use it. Read `RepoControllerTests.cs` first and pick the lower-friction option.)

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~RepoControllerCompareTests` → build error (no `Compare`).

- [ ] **Step 3: Add the endpoint.** In `src/api/TectikaAgents.Api/Controllers/RepoController.cs`, add (after the existing `pulls` endpoints):

```csharp
    [HttpGet("compare")]
    public async Task<IActionResult> Compare(string boardId, [FromQuery] string? @base, [FromQuery] string head, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        if (string.IsNullOrEmpty(head)) return BadRequest(new { error = "head is required" });
        var b = string.IsNullOrEmpty(@base) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @base;
        return Ok(await _gh.CompareAsync(repo!, b, head, ct));
    }
```

- [ ] **Step 4: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~RepoControllerCompareTests` → 2 pass. Then `dotnet build src/api/TectikaAgents.Api` (0 errors) and `dotnet test tests/TectikaAgents.Tests` (full suite passes).

- [ ] **Step 5: Commit:**
```bash
git add src/api/TectikaAgents.Api/Controllers/RepoController.cs tests/TectikaAgents.Tests/RepoControllerCompareTests.cs
git commit -m "feat(api): GET /repo/compare endpoint (base...head diff)"
```

---

## Task 7: Framework directive — instruct agents to push + open a PR

**Files:**
- Modify: `src/workflows/Services/ContextManager.cs`
- Test: `tests/TectikaAgents.Tests/ContextManagerTests.cs`

- [ ] **Step 1: Write the failing test.** In `tests/TectikaAgents.Tests/ContextManagerTests.cs`, add a `[Fact]` (build the assembled context the same way the existing tests in that file do — reuse their fixtures/helpers; read the file first):

```csharp
    [Fact]
    public void Context_InstructsAgentToPushBranchAndOpenPr()
    {
        // (Build the assembled round-0 `context` string exactly as the sibling tests do.)
        Assert.Contains("push", context);
        Assert.Contains("pull request", context);
    }
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~ContextManagerTests` → the new test fails.

- [ ] **Step 3: Update the directive.** In `src/workflows/Services/ContextManager.cs`, the workspace/sandbox guidance currently mentions git (`git commit`/`git push`). Augment it so agents are explicitly told to push and open a PR. Find the sandbox guidance line that mentions a connected repo (it reads roughly: `"...is cloned to /workspace with git configured (you can git commit/git push)."`) and extend that branch's text to:
```csharp
                  "and git configured. When you change code, commit it and run `git push` to your branch, " +
                  "and open a pull request (e.g. via the github_create_pr tool) so your change is reviewable. " +
```
(Keep the surrounding string structure intact — only extend the connected-repo guidance so the assembled context contains both the word `push` and the phrase `pull request`. If the existing wording already contains `push`, ensure `pull request` is added.)

- [ ] **Step 4: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~ContextManagerTests` → passes. Then full suite: `dotnet test tests/TectikaAgents.Tests`.

- [ ] **Step 5: Commit:**
```bash
git add src/workflows/Services/ContextManager.cs tests/TectikaAgents.Tests/ContextManagerTests.cs
git commit -m "feat(workflows): direct agents to push their branch and open a PR"
```

---

## Self-Review

**Spec coverage (against `2026-06-17-task-code-deliverable-design.md`):**
- §5.1 `CompareAsync` + DTOs + Octokit + cache → Task 1. ✓
- §5.2 `ExternalRef.Locator` → `Dictionary<string,string>`; `WorkflowRun` branch/PR → Tasks 2, 3. ✓
- §5.3 finalization push + automatic enrichment (`CompareAsync` + PR lookup + `CodeOutputBuilder`) + workflows DI → Tasks 4, 5. ✓
- §5.4 `/repo/compare` endpoint → Task 6. ✓
- §4 push/PR framework directive → Task 7. ✓
- §7 graceful errors (no repo / 0 files / push fail → no Code output; never blocks the run) → Task 5 (`Try*` best-effort helpers). ✓
- §8 tests: compare mapper, `CodeOutputBuilder`, RepoController compare contract, directive → Tasks 1, 4, 6, 7. ✓
- **Out of scope** (frontend Code card / Changes tab / diff renderer) → Plan 3B. Not a gap.

**Placeholder scan:** Every code step has full code. The two "read the existing file" notes (Task 6 `FakeCosmosForRepo` reuse, Task 7 ContextManager fixture/wording) are necessary because they depend on project-specific test scaffolding and the exact current sentence; the assertions + added text are given.

**Type consistency:** `CompareResult`/`DiffFile` (Task 1) used identically in `CompareAsync` (Task 1), `CodeOutputBuilder` (Task 4), the activity (Task 5), and the controller (Task 6). `CodeOutputBuilder.Build(repo, base, head, cmp, pr)` signature matches its call in Task 5 and its test in Task 4. `PatchRunBranchAsync(runId, branch, prNumber?)` matches its call in Task 5. The locator keys written by `CodeOutputBuilder` (Task 4) are what Plan 3B's Code card will read.
