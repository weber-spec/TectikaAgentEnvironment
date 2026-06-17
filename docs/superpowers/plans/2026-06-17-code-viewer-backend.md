# Code Viewer — Plan 2A (Backend: GitHub read service + API) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose read-only GitHub repository data (metadata, branches, tree, file, commits, pull requests) for a board through a typed service and board-scoped API endpoints, with short-TTL caching.

**Architecture:** A pure mapping helper + DTOs, a typed `IGitHubReadService` (`OctokitGitHubReadService` calling Octokit), a caching decorator, and a `RepoController` that loads the board, resolves its PAT, and serves the read service. A shared `GitHubClientFactory` deduplicates PAT-resolve + client construction (also adopted by the existing tool executor).

**Tech Stack:** C# / .NET 10, Octokit 13.0.1, ASP.NET Core, `Microsoft.Extensions.Caching.Memory`, xUnit (`tests/TectikaAgents.Tests`).

**Builds on:** Spec 1 (merged). **Scope note:** Plan 2A of two for Spec 2 (`docs/superpowers/specs/2026-06-17-code-viewer-design.md`). Plan 2B (frontend `RepoView`: tab, file tree/viewer, History, PRs, Shiki, api client, types, states) is the follow-on. Diffs/compare, Code outputs, and live preview are later specs (spec §3, §10).

---

## File Structure

**Create:**
- `src/agentruntime/GitHub/GitHubClientFactory.cs` — static: PAT resolve + `GitHubClient` construction.
- `src/agentruntime/GitHub/GitHubReadModels.cs` — read DTOs (`RepoMeta`, `BranchInfo`, `TreeEntry`, `FileContent`, `CommitInfo`, `PullRequestInfo`) + `IGitHubReadService`.
- `src/agentruntime/GitHub/GitHubReadMapping.cs` — pure `DecodeBlob(encodedContent, size)` (binary detection + text decode).
- `src/agentruntime/GitHub/OctokitGitHubReadService.cs` — Octokit implementation.
- `src/agentruntime/GitHub/CachedGitHubReadService.cs` — `IMemoryCache` decorator.
- `src/api/TectikaAgents.Api/Controllers/RepoController.cs` — board-scoped read endpoints.
- `tests/TectikaAgents.Tests/GitHubReadMappingTests.cs`
- `tests/TectikaAgents.Tests/RepoControllerTests.cs`
- `tests/TectikaAgents.Tests/CachedGitHubReadServiceTests.cs`

**Modify:**
- `src/agentruntime/GitHub/OctokitGitHubToolExecutor.cs` — use `GitHubClientFactory`.
- `src/api/TectikaAgents.Api/Program.cs` — register `IGitHubReadService` (Octokit + cache decorator) and `AddMemoryCache`.

---

## Task 1: `GitHubClientFactory` (shared PAT-resolve + client)

**Files:**
- Create: `src/agentruntime/GitHub/GitHubClientFactory.cs`
- Modify: `src/agentruntime/GitHub/OctokitGitHubToolExecutor.cs`

No new unit test (pure dedup of existing behavior; verified by build + the full suite, which exercises the executor).

- [ ] **Step 1: Create the factory.** `src/agentruntime/GitHub/GitHubClientFactory.cs`:

```csharp
using Octokit;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

/// <summary>Builds an authenticated Octokit client for a board's repo by resolving
/// its PAT through the secret provider. Shared by the read service and the tool executor.</summary>
public static class GitHubClientFactory
{
    public static async Task<GitHubClient> CreateAsync(ISecretProvider secrets, GitHubRepoConnection repo, CancellationToken ct)
    {
        var pat = await secrets.GetSecretAsync(repo.PatSecretName, ct);
        return new GitHubClient(new ProductHeaderValue("TectikaAgents"))
        {
            Credentials = new Credentials(pat),
        };
    }
}
```

- [ ] **Step 2: Use it in the executor.** In `src/agentruntime/GitHub/OctokitGitHubToolExecutor.cs`, replace these lines inside `ExecuteAsync`:

```csharp
            var pat = await _secrets.GetSecretAsync(boardRepo.PatSecretName, ct);
            var client = new GitHubClient(new ProductHeaderValue("TectikaAgents"))
            {
                Credentials = new Credentials(pat)
            };
```
with:
```csharp
            var client = await GitHubClientFactory.CreateAsync(_secrets, boardRepo, ct);
```
(Leave the rest of the method, the `_secrets` field, and the constructor unchanged.)

- [ ] **Step 3: Build + full suite.** `dotnet build src/agentruntime` → 0 errors; `dotnet test tests/TectikaAgents.Tests` → all pass (no regressions).

- [ ] **Step 4: Commit:**
```bash
git add src/agentruntime/GitHub/GitHubClientFactory.cs src/agentruntime/GitHub/OctokitGitHubToolExecutor.cs
git commit -m "refactor(agentruntime): extract GitHubClientFactory shared by tool executor"
```

---

## Task 2: Read DTOs + `IGitHubReadService` + binary-detection mapping

**Files:**
- Create: `src/agentruntime/GitHub/GitHubReadModels.cs`, `src/agentruntime/GitHub/GitHubReadMapping.cs`
- Test: `tests/TectikaAgents.Tests/GitHubReadMappingTests.cs`

- [ ] **Step 1: Write the failing test.** `tests/TectikaAgents.Tests/GitHubReadMappingTests.cs`:

```csharp
using System;
using System.Text;
using TectikaAgents.AgentRuntime.GitHub;
using Xunit;

public class GitHubReadMappingTests
{
    private static string B64(byte[] b) => Convert.ToBase64String(b);

    [Fact]
    public void DecodeBlob_TextContent_ReturnsTextNotBinary()
    {
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(B64(Encoding.UTF8.GetBytes("hello\nworld")), 11);
        Assert.False(isBinary);
        Assert.Equal("hello\nworld", text);
    }

    [Fact]
    public void DecodeBlob_NulByte_IsBinary_TextNull()
    {
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(B64(new byte[] { 1, 2, 0, 3 }), 4);
        Assert.True(isBinary);
        Assert.Null(text);
    }

    [Fact]
    public void DecodeBlob_OverSizeThreshold_IsBinary_TextNull()
    {
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(B64(Encoding.UTF8.GetBytes("small")), 2_000_000);
        Assert.True(isBinary);
        Assert.Null(text);
    }

    [Fact]
    public void DecodeBlob_NullOrEmptyEncoded_ReturnsEmptyText()
    {
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(null, 0);
        Assert.False(isBinary);
        Assert.Equal("", text);
    }
}
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~GitHubReadMappingTests` → build error.

- [ ] **Step 3: Create the DTOs + interface.** `src/agentruntime/GitHub/GitHubReadModels.cs`:

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

public sealed record RepoMeta(string DefaultBranch, string? Description, bool Private);
public sealed record BranchInfo(string Name, string CommitSha);
public sealed record TreeEntry(string Name, string Path, string Type, long Size); // Type: "file" | "dir"
public sealed record FileContent(string Path, string Sha, long Size, bool IsBinary, string? Text);
public sealed record CommitInfo(string Sha, string Message, string Author, DateTimeOffset Date, string Url);
public sealed record PullRequestInfo(int Number, string Title, string State, string Author, string Head, string Base, string Url, DateTimeOffset CreatedAt);

/// <summary>Read-only GitHub repository access for a board's connected repo.</summary>
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

- [ ] **Step 4: Create the mapping helper.** `src/agentruntime/GitHub/GitHubReadMapping.cs`:

```csharp
using System.Text;

namespace TectikaAgents.AgentRuntime.GitHub;

/// <summary>Pure helpers for mapping raw GitHub blob data to read DTOs.</summary>
public static class GitHubReadMapping
{
    public const long MaxTextBytes = 1_000_000; // 1 MB: above this we treat as binary (show "view on GitHub")

    /// <summary>Decode a base64 blob into (isBinary, text). Binary when it exceeds the size
    /// threshold or contains a NUL byte; text is null in that case. Null/empty encoded → ("", not binary).</summary>
    public static (bool IsBinary, string? Text) DecodeBlob(string? encodedContent, long size)
    {
        if (string.IsNullOrEmpty(encodedContent)) return (false, "");
        if (size > MaxTextBytes) return (true, null);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encodedContent); }
        catch (FormatException) { return (false, encodedContent); } // already-decoded text

        if (bytes.Length > MaxTextBytes) return (true, null);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return (true, null);
        return (false, Encoding.UTF8.GetString(bytes));
    }
}
```

- [ ] **Step 5: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~GitHubReadMappingTests` → 4 pass.

- [ ] **Step 6: Commit:**
```bash
git add src/agentruntime/GitHub/GitHubReadModels.cs src/agentruntime/GitHub/GitHubReadMapping.cs tests/TectikaAgents.Tests/GitHubReadMappingTests.cs
git commit -m "feat(agentruntime): add GitHub read DTOs, IGitHubReadService, blob mapping"
```

---

## Task 3: `OctokitGitHubReadService`

**Files:**
- Create: `src/agentruntime/GitHub/OctokitGitHubReadService.cs`

Thin Octokit glue (network calls). No unit test (would hit GitHub); the mapping is tested in Task 2 and the controller/cache logic in Tasks 4–5. Verified by build.

- [ ] **Step 1: Create the service.** `src/agentruntime/GitHub/OctokitGitHubReadService.cs`:

```csharp
using Octokit;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

public sealed class OctokitGitHubReadService : IGitHubReadService
{
    private readonly ISecretProvider _secrets;
    public OctokitGitHubReadService(ISecretProvider secrets) => _secrets = secrets;

    private Task<GitHubClient> ClientAsync(GitHubRepoConnection repo, CancellationToken ct) =>
        GitHubClientFactory.CreateAsync(_secrets, repo, ct);

    public async Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection repo, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var r = await c.Repository.Get(repo.Owner, repo.Repo);
        return new RepoMeta(r.DefaultBranch, r.Description, r.Private);
    }

    public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection repo, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var branches = await c.Repository.Branch.GetAll(repo.Owner, repo.Repo);
        return branches.Select(b => new BranchInfo(b.Name, b.Commit.Sha)).ToList();
    }

    public async Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        try
        {
            var items = string.IsNullOrEmpty(path)
                ? await c.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, @ref)
                : await c.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, path, @ref);
            return items
                .Select(i => new TreeEntry(i.Name, i.Path, i.Type.Value == ContentType.Dir ? "dir" : "file", i.Size))
                .OrderBy(e => e.Type == "file") // dirs first
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (NotFoundException) { return Array.Empty<TreeEntry>(); }
    }

    public async Task<FileContent> GetFileAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var items = await c.Repository.Content.GetAllContentsByRef(repo.Owner, repo.Repo, path, @ref);
        var f = items.FirstOrDefault()
            ?? throw new NotFoundException($"File '{path}' not found on '{@ref}'.", System.Net.HttpStatusCode.NotFound);
        var (isBinary, text) = GitHubReadMapping.DecodeBlob(f.EncodedContent, f.Size);
        return new FileContent(f.Path, f.Sha, f.Size, isBinary, text);
    }

    public async Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection repo, string @ref, string? path, int page, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var request = new CommitRequest { Sha = @ref };
        if (!string.IsNullOrEmpty(path)) request.Path = path;
        var options = new ApiOptions { PageSize = 30, PageCount = 1, StartPage = page < 1 ? 1 : page };
        var commits = await c.Repository.Commit.GetAll(repo.Owner, repo.Repo, request, options);
        return commits.Select(gc => new CommitInfo(
            gc.Sha,
            gc.Commit.Message,
            gc.Commit.Author?.Name ?? gc.Author?.Login ?? "unknown",
            gc.Commit.Author?.Date ?? default,
            gc.HtmlUrl)).ToList();
    }

    public async Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection repo, string state, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        var request = new PullRequestRequest
        {
            State = state?.ToLowerInvariant() switch
            {
                "closed" => ItemStateFilter.Closed,
                "all" => ItemStateFilter.All,
                _ => ItemStateFilter.Open,
            },
        };
        var prs = await c.PullRequest.GetAllForRepository(repo.Owner, repo.Repo, request);
        return prs.Select(Map).ToList();
    }

    public async Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection repo, int number, CancellationToken ct)
    {
        var c = await ClientAsync(repo, ct);
        try { return Map(await c.PullRequest.Get(repo.Owner, repo.Repo, number)); }
        catch (NotFoundException) { return null; }
    }

    private static PullRequestInfo Map(PullRequest p) => new(
        p.Number, p.Title, p.State.StringValue ?? p.State.ToString(),
        p.User?.Login ?? "unknown", p.Head?.Ref ?? "", p.Base?.Ref ?? "", p.HtmlUrl, p.CreatedAt);
}
```

- [ ] **Step 2: Build** — `dotnet build src/agentruntime` → 0 errors. (If any Octokit member name differs in 13.0.1, adjust to the compiler's guidance — the shapes mirror the existing `OctokitGitHubToolExecutor` usage.)

- [ ] **Step 3: Commit:**
```bash
git add src/agentruntime/GitHub/OctokitGitHubReadService.cs
git commit -m "feat(agentruntime): Octokit implementation of IGitHubReadService"
```

---

## Task 4: `CachedGitHubReadService` decorator

**Files:**
- Create: `src/agentruntime/GitHub/CachedGitHubReadService.cs`
- Test: `tests/TectikaAgents.Tests/CachedGitHubReadServiceTests.cs`

- [ ] **Step 1: Write the failing test.** `tests/TectikaAgents.Tests/CachedGitHubReadServiceTests.cs`:

```csharp
using Microsoft.Extensions.Caching.Memory;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;
using Xunit;

public class CachedGitHubReadServiceTests
{
    private sealed class CountingInner : IGitHubReadService
    {
        public int BranchCalls;
        public Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult(new RepoMeta("main", null, false));
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection r, CancellationToken ct)
        { BranchCalls++; return Task.FromResult<IReadOnlyList<BranchInfo>>(new[] { new BranchInfo("main", "abc") }); }
        public Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult<IReadOnlyList<TreeEntry>>(System.Array.Empty<TreeEntry>());
        public Task<FileContent> GetFileAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult(new FileContent(p, "s", 0, false, ""));
        public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection r, string @ref, string? p, int page, CancellationToken ct) => Task.FromResult<IReadOnlyList<CommitInfo>>(System.Array.Empty<CommitInfo>());
        public Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection r, string s, CancellationToken ct) => Task.FromResult<IReadOnlyList<PullRequestInfo>>(System.Array.Empty<PullRequestInfo>());
        public Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection r, int n, CancellationToken ct) => Task.FromResult<PullRequestInfo?>(null);
    }

    private static GitHubRepoConnection Repo() => new() { Owner = "o", Repo = "r", PatSecretName = "s" };

    [Fact]
    public async Task SecondCall_WithinTtl_HitsCache_NotInner()
    {
        var inner = new CountingInner();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new CachedGitHubReadService(inner, cache);

        await svc.ListBranchesAsync(Repo(), default);
        await svc.ListBranchesAsync(Repo(), default);

        Assert.Equal(1, inner.BranchCalls); // second served from cache
    }

    [Fact]
    public async Task DifferentRepo_DoesNotShareCache()
    {
        var inner = new CountingInner();
        var svc = new CachedGitHubReadService(inner, new MemoryCache(new MemoryCacheOptions()));

        await svc.ListBranchesAsync(new GitHubRepoConnection { Owner = "o", Repo = "r1", PatSecretName = "s" }, default);
        await svc.ListBranchesAsync(new GitHubRepoConnection { Owner = "o", Repo = "r2", PatSecretName = "s" }, default);

        Assert.Equal(2, inner.BranchCalls);
    }
}
```

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~CachedGitHubReadServiceTests` → build error (no `CachedGitHubReadService`).

- [ ] **Step 3: Create the decorator.** `src/agentruntime/GitHub/CachedGitHubReadService.cs`:

```csharp
using Microsoft.Extensions.Caching.Memory;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.GitHub;

/// <summary>Short-TTL in-memory cache over an inner read service, to stay under
/// GitHub rate limits during interactive browsing. Keyed by repo + operation + args.</summary>
public sealed class CachedGitHubReadService : IGitHubReadService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly IGitHubReadService _inner;
    private readonly IMemoryCache _cache;

    public CachedGitHubReadService(IGitHubReadService inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    private Task<T> Cached<T>(string key, Func<Task<T>> factory) =>
        _cache.GetOrCreateAsync(key, entry => { entry.AbsoluteExpirationRelativeToNow = Ttl; return factory(); })!;

    private static string K(GitHubRepoConnection r, string op, params string[] parts) =>
        $"gh:{r.Owner}/{r.Repo}:{op}:{string.Join(':', parts)}";

    public Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection repo, CancellationToken ct) =>
        Cached(K(repo, "meta"), () => _inner.GetRepoMetadataAsync(repo, ct));

    public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection repo, CancellationToken ct) =>
        Cached(K(repo, "branches"), () => _inner.ListBranchesAsync(repo, ct));

    public Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct) =>
        Cached(K(repo, "tree", @ref, path), () => _inner.ListDirectoryAsync(repo, @ref, path, ct));

    public Task<FileContent> GetFileAsync(GitHubRepoConnection repo, string @ref, string path, CancellationToken ct) =>
        Cached(K(repo, "file", @ref, path), () => _inner.GetFileAsync(repo, @ref, path, ct));

    public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection repo, string @ref, string? path, int page, CancellationToken ct) =>
        Cached(K(repo, "commits", @ref, path ?? "", page.ToString()), () => _inner.ListCommitsAsync(repo, @ref, path, page, ct));

    public Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection repo, string state, CancellationToken ct) =>
        Cached(K(repo, "pulls", state), () => _inner.ListPullRequestsAsync(repo, state, ct));

    public Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection repo, int number, CancellationToken ct) =>
        Cached(K(repo, "pull", number.ToString()), () => _inner.GetPullRequestAsync(repo, number, ct));
}
```

- [ ] **Step 4: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~CachedGitHubReadServiceTests` → 2 pass.

- [ ] **Step 5: Commit:**
```bash
git add src/agentruntime/GitHub/CachedGitHubReadService.cs tests/TectikaAgents.Tests/CachedGitHubReadServiceTests.cs
git commit -m "feat(agentruntime): short-TTL caching decorator for IGitHubReadService"
```

---

## Task 5: `RepoController` + DI wiring

**Files:**
- Create: `src/api/TectikaAgents.Api/Controllers/RepoController.cs`
- Modify: `src/api/TectikaAgents.Api/Program.cs`
- Test: `tests/TectikaAgents.Tests/RepoControllerTests.cs`

- [ ] **Step 1: Write the failing test.** `tests/TectikaAgents.Tests/RepoControllerTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Controllers;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Core.Models;
using Xunit;

public class RepoControllerTests
{
    private sealed class FakeRead : IGitHubReadService
    {
        public Task<RepoMeta> GetRepoMetadataAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult(new RepoMeta("main", null, false));
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(GitHubRepoConnection r, CancellationToken ct) => Task.FromResult<IReadOnlyList<BranchInfo>>(new[] { new BranchInfo("main", "abc") });
        public Task<IReadOnlyList<TreeEntry>> ListDirectoryAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult<IReadOnlyList<TreeEntry>>(System.Array.Empty<TreeEntry>());
        public Task<FileContent> GetFileAsync(GitHubRepoConnection r, string @ref, string p, CancellationToken ct) => Task.FromResult(new FileContent(p, "s", 0, false, ""));
        public Task<IReadOnlyList<CommitInfo>> ListCommitsAsync(GitHubRepoConnection r, string @ref, string? p, int page, CancellationToken ct) => Task.FromResult<IReadOnlyList<CommitInfo>>(System.Array.Empty<CommitInfo>());
        public Task<IReadOnlyList<PullRequestInfo>> ListPullRequestsAsync(GitHubRepoConnection r, string s, CancellationToken ct) => Task.FromResult<IReadOnlyList<PullRequestInfo>>(System.Array.Empty<PullRequestInfo>());
        public Task<PullRequestInfo?> GetPullRequestAsync(GitHubRepoConnection r, int n, CancellationToken ct) => Task.FromResult<PullRequestInfo?>(null);
    }

    private static RepoController Make(Board? board)
    {
        var cosmos = new FakeCosmosForRepo(board);
        var ctrl = new RepoController(cosmos, new FakeRead());
        // no auth context in unit test → TenantId falls back to "default"
        ctrl.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        return ctrl;
    }

    [Fact]
    public async Task Branches_BoardNotFound_Returns404()
    {
        var result = await Make(null).Branches("missing", default);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Branches_NoGitHub_Returns409Typed()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = null };
        var result = await Make(board).Branches("b1", default);
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("GitHubNotConnected", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task Branches_Connected_ReturnsOkWithData()
    {
        var board = new Board { Id = "b1", TenantId = "default", GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", PatSecretName = "s" } };
        var result = await Make(board).Branches("b1", default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var branches = Assert.IsAssignableFrom<IReadOnlyList<BranchInfo>>(ok.Value);
        Assert.Single(branches);
    }
}
```

Also create the minimal fake cosmos in the same file (only the member the controller uses):

```csharp
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Models;

public sealed class FakeCosmosForRepo : ICosmosDbService
{
    private readonly Board? _board;
    public FakeCosmosForRepo(Board? board) => _board = board;
    public Task<Board?> GetBoardAsync(string tenantId, string boardId, CancellationToken ct = default) => Task.FromResult(_board);
    // All other ICosmosDbService members throw — unused by RepoController.
    public Task<T> ___unused<T>() => throw new System.NotImplementedException();
}
```

> NOTE for the implementer: `ICosmosDbService` has many members. Rather than hand-stub them all, make `FakeCosmosForRepo` extend a test base if one exists, OR (preferred) implement the interface explicitly and throw `NotImplementedException` for every member except `GetBoardAsync`. Read `src/api/TectikaAgents.Api/Services/ICosmosDbService.cs` to get the exact member list and generate the stub. Keep it in the test file.

- [ ] **Step 2: Run, verify FAIL** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~RepoControllerTests` → build error (no `RepoController`).

- [ ] **Step 3: Create the controller.** `src/api/TectikaAgents.Api/Controllers/RepoController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.AgentRuntime.GitHub;
using TectikaAgents.Api.Services;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/boards/{boardId}/repo")]
[Authorize]
public class RepoController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly IGitHubReadService _gh;

    public RepoController(ICosmosDbService cosmos, IGitHubReadService gh)
    {
        _cosmos = cosmos;
        _gh = gh;
    }

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    // Loads the board and its GitHub connection, or returns the right error result.
    private async Task<(Core.Models.GitHubRepoConnection? repo, IActionResult? error)> ResolveAsync(string boardId, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(TenantId, boardId, ct);
        if (board is null) return (null, NotFound());
        if (board.GitHub is null) return (null, Conflict(new { error = "GitHubNotConnected" }));
        return (board.GitHub, null);
    }

    [HttpGet("meta")]
    public async Task<IActionResult> Meta(string boardId, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        return Ok(await _gh.GetRepoMetadataAsync(repo!, ct));
    }

    [HttpGet("branches")]
    public async Task<IActionResult> Branches(string boardId, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        return Ok(await _gh.ListBranchesAsync(repo!, ct));
    }

    [HttpGet("tree")]
    public async Task<IActionResult> Tree(string boardId, [FromQuery] string? @ref, [FromQuery] string? path, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        var r = string.IsNullOrEmpty(@ref) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @ref;
        return Ok(await _gh.ListDirectoryAsync(repo!, r, path ?? "", ct));
    }

    [HttpGet("file")]
    public async Task<IActionResult> File(string boardId, [FromQuery] string? @ref, [FromQuery] string path, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        var r = string.IsNullOrEmpty(@ref) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @ref;
        return Ok(await _gh.GetFileAsync(repo!, r, path, ct));
    }

    [HttpGet("commits")]
    public async Task<IActionResult> Commits(string boardId, [FromQuery] string? @ref, [FromQuery] string? path, [FromQuery] int page, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        var r = string.IsNullOrEmpty(@ref) ? (await _gh.GetRepoMetadataAsync(repo!, ct)).DefaultBranch : @ref;
        return Ok(await _gh.ListCommitsAsync(repo!, r, path, page < 1 ? 1 : page, ct));
    }

    [HttpGet("pulls")]
    public async Task<IActionResult> Pulls(string boardId, [FromQuery] string? state, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        return Ok(await _gh.ListPullRequestsAsync(repo!, state ?? "open", ct));
    }

    [HttpGet("pulls/{number:int}")]
    public async Task<IActionResult> Pull(string boardId, int number, CancellationToken ct)
    {
        var (repo, error) = await ResolveAsync(boardId, ct);
        if (error is not null) return error;
        var pr = await _gh.GetPullRequestAsync(repo!, number, ct);
        return pr is null ? NotFound() : Ok(pr);
    }
}
```

- [ ] **Step 4: Register DI.** In `src/api/TectikaAgents.Api/Program.cs`, immediately after the line `builder.Services.AddSingleton<IGitHubToolExecutor, OctokitGitHubToolExecutor>();`, add:

```csharp
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OctokitGitHubReadService>();
builder.Services.AddSingleton<IGitHubReadService>(sp =>
    new CachedGitHubReadService(
        sp.GetRequiredService<OctokitGitHubReadService>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
```
Add `using TectikaAgents.AgentRuntime.GitHub;` to the top of `Program.cs` if not present.

- [ ] **Step 5: Run, verify PASS** — `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~RepoControllerTests` → 3 pass. Then `dotnet build src/api/TectikaAgents.Api` → 0 errors, and `dotnet test tests/TectikaAgents.Tests` → full suite passes.

- [ ] **Step 6: Commit:**
```bash
git add src/api/TectikaAgents.Api/Controllers/RepoController.cs src/api/TectikaAgents.Api/Program.cs tests/TectikaAgents.Tests/RepoControllerTests.cs
git commit -m "feat(api): RepoController board-scoped GitHub read endpoints + DI wiring"
```

---

## Self-Review

**Spec coverage (against `2026-06-17-code-viewer-design.md`):**
- §4.1 `IGitHubReadService` (meta/branches/tree/file/commits/pulls) + DTOs + binary detection + shared client factory → Tasks 1, 2, 3. ✓
- §4.2 `RepoController` board-scoped endpoints + `409 GitHubNotConnected` + ref/path/state defaulting → Task 5. ✓
- §4.3 short-TTL caching → Task 4 (decorator) + Task 5 DI. ✓
- §6 errors: no-GitHub (409), not-found (404 / empty), binary (IsBinary/Text null) → Tasks 2, 3, 5. ✓
- §7 testing: mapping unit tests (Task 2), controller contract tests incl. 404/409/happy (Task 5), cache test (Task 4). ✓ (Octokit service is thin glue, build-verified, per §7's "behind an abstraction" intent satisfied by the tested mapping seam.)
- **Out of scope** (frontend RepoView, diffs, Code outputs) → correctly deferred to Plan 2B / Spec 3. Not a gap.

**Placeholder scan:** Every code step has full code. The one "read the file to generate the stub" instruction (Task 5 fake cosmos) is unavoidable — `ICosmosDbService` is large and project-specific; the controller uses only `GetBoardAsync`, and the instruction is explicit.

**Type consistency:** DTO names/shapes (`RepoMeta`/`BranchInfo`/`TreeEntry`/`FileContent`/`CommitInfo`/`PullRequestInfo`) and `IGitHubReadService` signatures are identical across Tasks 2 (def), 3 (impl), 4 (decorator), 5 (controller + tests). `GitHubReadMapping.DecodeBlob` signature matches its call in Task 3. `409`/`GitHubNotConnected` shape matches between controller (Task 5) and its test.
