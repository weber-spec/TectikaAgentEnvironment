# Live Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user start an ephemeral, public, auto-idling preview of the agent-built web app for any branch, from the board Repo view.

**Architecture:** API control plane (`PreviewController` + `PreviewService` + `PreviewIdleReaperService`) writes `PreviewSession` state to Cosmos and drives an `AciPreviewProvisioner` (in AgentRuntime) that creates a dedicated ACI container running a new `preview-runner` image. The container clones the branch, installs deps, and serves the app on a public random-DNS FQDN:8080. The browser heartbeats to keep it alive; a reaper tears down expired/orphan containers.

**Tech Stack:** .NET 9/10, Azure.ResourceManager.ContainerInstance, Cosmos (System.Text.Json), xUnit; Next.js 16 / React 19 / TS (`node --test`); Docker (node:20-slim); Azure ACI + ACR + Bicep.

**Spec:** `docs/superpowers/specs/2026-06-22-live-preview-design.md`

**Worktree:** all paths are relative to the `feat/live-preview` worktree root.

---

## File structure

**Core (`src/core/TectikaAgents.Core`)**
- Create `Models/PreviewSession.cs` — `PreviewStatus` enum, `PreviewSession` model.
- Create `Models/PreviewLifecycle.cs` — pure expiry/heartbeat math + DNS-label naming.
- Create `Interfaces/IPreviewProvisioner.cs` — provisioner abstraction + `PreviewProvisionResult`.

**AgentRuntime (`src/agentruntime`)**
- Create `Preview/AciPreviewProvisioner.cs` — ACI adapter implementing `IPreviewProvisioner`.
- Modify `TectikaAgents.AgentRuntime.csproj` — add `Azure.ResourceManager.ContainerInstance`.

**API (`src/api/TectikaAgents.Api`)**
- Modify `Services/CosmosDbService.cs` — add `previewSessions` container + CRUD/query methods.
- Modify `Services/ICosmosDbService.cs` + `Services/InMemoryCosmosDbService.cs` — same methods.
- Create `Services/PreviewService.cs` + `Services/IPreviewService.cs` — orchestration / single writer.
- Create `Services/PreviewIdleReaperService.cs` — `BackgroundService`.
- Create `Controllers/PreviewController.cs` — REST endpoints.
- Modify `Program.cs` — DI for provisioner, preview service, reaper.

**Runner image**
- Create `docker/preview-runner/Dockerfile`, `docker/preview-runner/entrypoint.sh`.

**Frontend (`src/web/tectika-board`)**
- Modify `src/lib/types.ts` — `PreviewSession` + `PreviewStatus`.
- Modify `src/lib/api.ts` — `api.preview.*`.
- Create `src/components/board/repo/PreviewTab.tsx`.
- Modify `src/components/board/repo/RepoView.tsx` — `'preview'` sub-tab.

**Infra / deploy**
- Modify `scripts/deploy.sh` + `scripts/deploy.ps1` — build/push `preview-runner`.
- Modify `infra/` bicep — Cosmos container, API-MI ACI role, app settings.

**Tests (`tests/TectikaAgents.Tests`)**
- `PreviewLifecycleTests.cs`, `PreviewServiceTests.cs`, `PreviewReaperTests.cs`.
- Frontend: `src/web/tectika-board/src/lib/__tests__/preview-api.test.ts`.

---

## Phase A — Backend core

### Task 1: PreviewSession model + lifecycle math

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/PreviewSession.cs`
- Create: `src/core/TectikaAgents.Core/Models/PreviewLifecycle.cs`
- Test: `tests/TectikaAgents.Tests/PreviewLifecycleTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class PreviewLifecycleTests
{
    static readonly DateTimeOffset T0 = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Expiry_is_lastActivity_plus_idle_when_under_cap()
    {
        var exp = PreviewLifecycle.ComputeExpiry(createdAt: T0, lastActivityAt: T0.AddMinutes(5),
            idleMinutes: 15, capMinutes: 45);
        Assert.Equal(T0.AddMinutes(20), exp); // 5 + 15
    }

    [Fact]
    public void Expiry_is_capped_at_createdAt_plus_cap()
    {
        var exp = PreviewLifecycle.ComputeExpiry(createdAt: T0, lastActivityAt: T0.AddMinutes(40),
            idleMinutes: 15, capMinutes: 45);
        Assert.Equal(T0.AddMinutes(45), exp); // 40 + 15 = 55, capped to 45
    }

    [Fact]
    public void IsExpired_true_when_now_past_expiry()
    {
        var s = new PreviewSession { CreatedAt = T0, ExpiresAt = T0.AddMinutes(10) };
        Assert.True(PreviewLifecycle.IsExpired(s, T0.AddMinutes(11)));
        Assert.False(PreviewLifecycle.IsExpired(s, T0.AddMinutes(9)));
    }

    [Fact]
    public void NewDnsLabel_has_prefix_and_is_lowercase_alnum()
    {
        var label = PreviewLifecycle.NewDnsLabel();
        Assert.StartsWith("tpv-", label);
        Assert.Matches("^tpv-[0-9a-f]{12}$", label);
        Assert.NotEqual(PreviewLifecycle.NewDnsLabel(), label); // unguessable / unique
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~PreviewLifecycleTests"`
Expected: FAIL (PreviewSession / PreviewLifecycle not defined).

- [ ] **Step 3: Write the model**

`src/core/TectikaAgents.Core/Models/PreviewSession.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

public enum PreviewStatus { Provisioning, Running, Failed, Stopped }

public class PreviewSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty; // == DNS label == ACI group name

    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = string.Empty; // partition key

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PreviewStatus Status { get; set; } = PreviewStatus.Provisioning;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastActivityAt")]
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`src/core/TectikaAgents.Core/Models/PreviewLifecycle.cs`:

```csharp
namespace TectikaAgents.Core.Models;

/// <summary>Pure lifecycle math + naming for previews (no I/O, fully unit-testable).</summary>
public static class PreviewLifecycle
{
    /// <summary>expiry = min(lastActivity + idle, createdAt + cap).</summary>
    public static DateTimeOffset ComputeExpiry(
        DateTimeOffset createdAt, DateTimeOffset lastActivityAt, int idleMinutes, int capMinutes)
    {
        var idle = lastActivityAt.AddMinutes(idleMinutes);
        var cap = createdAt.AddMinutes(capMinutes);
        return idle < cap ? idle : cap;
    }

    public static bool IsExpired(PreviewSession s, DateTimeOffset now) => now >= s.ExpiresAt;

    /// <summary>Unguessable, DNS-label-safe name (also the ACI group name).</summary>
    public static string NewDnsLabel() => "tpv-" + Guid.NewGuid().ToString("n")[..12];
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~PreviewLifecycleTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/PreviewSession.cs src/core/TectikaAgents.Core/Models/PreviewLifecycle.cs tests/TectikaAgents.Tests/PreviewLifecycleTests.cs
git commit -m "feat(core): PreviewSession model + lifecycle math"
```

---

### Task 2: IPreviewProvisioner abstraction

**Files:**
- Create: `src/core/TectikaAgents.Core/Interfaces/IPreviewProvisioner.cs`

- [ ] **Step 1: Write the interface** (no test — pure abstraction, exercised via Task 5 mocks)

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Outcome of provisioning a preview container group.</summary>
public sealed record PreviewProvisionResult(string Fqdn, string ContainerName);

/// <summary>A summary of a live preview container group (for orphan reconciliation).</summary>
public sealed record PreviewGroupInfo(string Name, string BoardId);

/// <summary>Provisions and tears down ephemeral preview container groups (ACI in prod).</summary>
public interface IPreviewProvisioner
{
    /// <summary>Create a public container group serving the branch on port 8080.
    /// dnsLabel becomes the group name AND the public DNS label. Returns the FQDN (no scheme/port).</summary>
    Task<PreviewProvisionResult> ProvisionAsync(
        GitHubRepoConnection repo, string branch, string? pat, string dnsLabel, CancellationToken ct);

    /// <summary>Delete a container group by name. Idempotent (missing group is a no-op).</summary>
    Task DestroyAsync(string containerName, CancellationToken ct);

    /// <summary>List all preview container groups (tagged tectika-preview) for orphan cleanup.</summary>
    Task<IReadOnlyList<PreviewGroupInfo>> ListPreviewGroupsAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Interfaces/IPreviewProvisioner.cs
git commit -m "feat(core): IPreviewProvisioner abstraction"
```

---

### Task 3: AciPreviewProvisioner (ACI adapter)

**Files:**
- Create: `src/agentruntime/Preview/AciPreviewProvisioner.cs`
- Modify: `src/agentruntime/TectikaAgents.AgentRuntime.csproj`

Reference implementation to mirror: `src/workflows/Services/WorkspaceService.cs` (ACI create/FQDN/destroy).

- [ ] **Step 1: Add the package**

In `src/agentruntime/TectikaAgents.AgentRuntime.csproj`, add inside the existing `<ItemGroup>` of PackageReferences (match the version Workflows uses — check `src/workflows/*.csproj`):

```xml
<PackageReference Include="Azure.ResourceManager.ContainerInstance" Version="1.2.1" />
```

(If Workflows pins a different version, use that exact version for consistency.)

- [ ] **Step 2: Write the adapter**

`src/agentruntime/Preview/AciPreviewProvisioner.cs`:

```csharp
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime.Preview;

public sealed class AciPreviewOptions
{
    public string ResourceGroup { get; set; } = string.Empty;
    public string Region { get; set; } = "westeurope";
    public string AcrImage { get; set; } = string.Empty;       // tacragentteam.azurecr.io/preview-runner:latest
    public string AcrLoginServer { get; set; } = "tacragentteam.azurecr.io";
    public string? MiResourceId { get; set; }                  // UAMI for ACR pull
}

public sealed class AciPreviewProvisioner : IPreviewProvisioner
{
    private const int AppPort = 8080;
    private const string PreviewTagKey = "tectika-preview";
    private readonly AciPreviewOptions _opt;
    private readonly ILogger<AciPreviewProvisioner> _log;

    public AciPreviewProvisioner(AciPreviewOptions opt, ILogger<AciPreviewProvisioner> log)
    { _opt = opt; _log = log; }

    private async Task<ResourceGroupResource> RgAsync(CancellationToken ct)
    {
        var arm = new ArmClient(new DefaultAzureCredential());
        var sub = await arm.GetDefaultSubscriptionAsync(ct);
        return (await sub.GetResourceGroupAsync(_opt.ResourceGroup, ct)).Value;
    }

    public async Task<PreviewProvisionResult> ProvisionAsync(
        GitHubRepoConnection repo, string branch, string? pat, string dnsLabel, CancellationToken ct)
    {
        var container = new ContainerInstanceContainer(
            "preview", _opt.AcrImage,
            new ContainerResourceRequirements(new ContainerResourceRequestsContent(2, 1)))
        {
            Ports = { new ContainerPort(AppPort) },
        };
        container.EnvironmentVariables.Add(new("REPO_URL") { Value = repo.RepoUrl });
        container.EnvironmentVariables.Add(new("GIT_BRANCH") { Value = branch });
        if (!string.IsNullOrEmpty(pat))
            container.EnvironmentVariables.Add(new("GIT_PAT") { SecureValue = pat });

        var data = new ContainerGroupData(
            new AzureLocation(_opt.Region), new[] { container },
            ContainerInstanceOperatingSystemType.Linux)
        {
            RestartPolicy = ContainerGroupRestartPolicy.Never,
            IPAddress = new ContainerGroupIPAddress(
                new[] { new ContainerGroupPort(AppPort) { Protocol = ContainerGroupNetworkProtocol.Tcp } },
                ContainerGroupIPAddressType.Public)
            { DnsNameLabel = dnsLabel },
        };
        data.Tags[PreviewTagKey] = repo.Repo; // value not critical; presence marks a preview
        data.Tags["boardOwner"] = repo.Owner;

        if (!string.IsNullOrEmpty(_opt.MiResourceId))
        {
            data.Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.UserAssigned);
            data.Identity.UserAssignedIdentities[new ResourceIdentifier(_opt.MiResourceId)] = new UserAssignedIdentity();
            data.ImageRegistryCredentials.Add(
                new ContainerGroupImageRegistryCredential(_opt.AcrLoginServer) { Identity = _opt.MiResourceId });
        }

        var rg = await RgAsync(ct);
        var op = await rg.GetContainerGroups().CreateOrUpdateAsync(WaitUntil.Completed, dnsLabel, data, ct);
        var fqdn = op.Value.Data.IPAddress?.Fqdn ?? $"{dnsLabel}.{_opt.Region}.azurecontainer.io";
        _log.LogInformation("[Preview] provisioned {Name} -> {Fqdn}", dnsLabel, fqdn);
        return new PreviewProvisionResult(fqdn, dnsLabel);
    }

    public async Task DestroyAsync(string containerName, CancellationToken ct)
    {
        try
        {
            var rg = await RgAsync(ct);
            var group = (await rg.GetContainerGroupAsync(containerName, ct)).Value;
            await group.DeleteAsync(WaitUntil.Started, ct);
            _log.LogInformation("[Preview] destroyed {Name}", containerName);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _log.LogInformation("[Preview] destroy no-op, {Name} already gone", containerName);
        }
    }

    public async Task<IReadOnlyList<PreviewGroupInfo>> ListPreviewGroupsAsync(CancellationToken ct)
    {
        var rg = await RgAsync(ct);
        var result = new List<PreviewGroupInfo>();
        await foreach (var g in rg.GetContainerGroups().GetAllAsync(ct))
        {
            if (g.Data.Tags.TryGetValue(PreviewTagKey, out _))
                result.Add(new PreviewGroupInfo(g.Data.Name, g.Data.Tags.GetValueOrDefault("boardOwner", "")));
        }
        return result;
    }
}
```

> NOTE for implementer: the ACI SDK surface (constructor arg shapes, `EnvironmentVariables`, `Tags`)
> can differ slightly by package version. Mirror the EXACT calls in
> `src/workflows/Services/WorkspaceService.cs` for the version in this repo; adjust the snippet to compile.

- [ ] **Step 3: Build**

Run: `dotnet build src/agentruntime/TectikaAgents.AgentRuntime.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/agentruntime/Preview/AciPreviewProvisioner.cs src/agentruntime/TectikaAgents.AgentRuntime.csproj
git commit -m "feat(agentruntime): AciPreviewProvisioner (ACI adapter)"
```

---

### Task 4: Cosmos previewSessions container + persistence

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/CosmosDbService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/ICosmosDbService.cs`
- Modify: `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs`

- [ ] **Step 1: Add container definition (CosmosDbService.cs)**

Add a constant near the other `*Container` consts:

```csharp
public const string PreviewSessionsContainer = "previewSessions";
```

Add to the `ContainerDefinitions` array:

```csharp
(PreviewSessionsContainer, "/boardId"),
```

- [ ] **Step 2: Add interface methods (ICosmosDbService.cs)**

```csharp
Task<PreviewSession?> GetPreviewAsync(string boardId, CancellationToken ct = default);
Task UpsertPreviewAsync(PreviewSession session, CancellationToken ct = default);
Task DeletePreviewAsync(string boardId, string id, CancellationToken ct = default);
Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default);
```

(`GetPreviewAsync` returns the single active session for a board — we keep one per board, id is queried.)

- [ ] **Step 3: Implement in CosmosDbService.cs**

```csharp
private Container PreviewContainer => _database.GetContainer(PreviewSessionsContainer);

public async Task<PreviewSession?> GetPreviewAsync(string boardId, CancellationToken ct = default)
{
    var q = new QueryDefinition(
        "SELECT * FROM c WHERE c.boardId = @b AND c.status IN ('Provisioning','Running') ORDER BY c.createdAt DESC")
        .WithParameter("@b", boardId);
    using var it = PreviewContainer.GetItemQueryIterator<PreviewSession>(
        q, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(boardId), MaxItemCount = 1 });
    if (it.HasMoreResults)
    {
        foreach (var s in await it.ReadNextAsync(ct)) return s;
    }
    return null;
}

public async Task UpsertPreviewAsync(PreviewSession session, CancellationToken ct = default) =>
    await PreviewContainer.UpsertItemAsync(session, new PartitionKey(session.BoardId), cancellationToken: ct);

public async Task DeletePreviewAsync(string boardId, string id, CancellationToken ct = default)
{
    try { await PreviewContainer.DeleteItemAsync<PreviewSession>(id, new PartitionKey(boardId), cancellationToken: ct); }
    catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { }
}

public async Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default)
{
    var q = new QueryDefinition("SELECT * FROM c WHERE c.status IN ('Provisioning','Running')");
    using var it = PreviewContainer.GetItemQueryIterator<PreviewSession>(q);
    var list = new List<PreviewSession>();
    while (it.HasMoreResults) list.AddRange(await it.ReadNextAsync(ct));
    return list;
}
```

(Match the existing field names this file uses for the `Container`/`Database` handle — e.g. `_database` or `_cosmosClient.GetDatabase(...)`. Mirror an existing method like `GetBoardAsync`.)

- [ ] **Step 4: Implement in InMemoryCosmosDbService.cs**

```csharp
private readonly List<PreviewSession> _previews = new();

public Task<PreviewSession?> GetPreviewAsync(string boardId, CancellationToken ct = default) =>
    Task.FromResult(_previews
        .Where(p => p.BoardId == boardId && (p.Status == PreviewStatus.Provisioning || p.Status == PreviewStatus.Running))
        .OrderByDescending(p => p.CreatedAt).FirstOrDefault());

public Task UpsertPreviewAsync(PreviewSession session, CancellationToken ct = default)
{
    _previews.RemoveAll(p => p.Id == session.Id);
    _previews.Add(session);
    return Task.CompletedTask;
}

public Task DeletePreviewAsync(string boardId, string id, CancellationToken ct = default)
{
    _previews.RemoveAll(p => p.Id == id && p.BoardId == boardId);
    return Task.CompletedTask;
}

public Task<IReadOnlyList<PreviewSession>> ListActivePreviewsAsync(CancellationToken ct = default) =>
    Task.FromResult((IReadOnlyList<PreviewSession>)_previews
        .Where(p => p.Status == PreviewStatus.Provisioning || p.Status == PreviewStatus.Running).ToList());
```

- [ ] **Step 5: Build**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/CosmosDbService.cs src/api/TectikaAgents.Api/Services/ICosmosDbService.cs src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs
git commit -m "feat(api): previewSessions Cosmos container + persistence"
```

---

### Task 5: PreviewService (orchestration / single writer)

**Files:**
- Create: `src/api/TectikaAgents.Api/Services/IPreviewService.cs`
- Create: `src/api/TectikaAgents.Api/Services/PreviewService.cs`
- Test: `tests/TectikaAgents.Tests/PreviewServiceTests.cs`

Behavior:
- `StartAsync(tenantId, boardId, branch)`: load board; if no `board.GitHub` → throw `PreviewNotConnectedException`. Tear down any existing active session for the board (replace). Create `PreviewSession{Provisioning}`, persist, then provision (await), then set `Running` + `Url` (or `Failed` + error), persist, return.
- `GetAsync(tenantId, boardId)`: return active session or null.
- `HeartbeatAsync(tenantId, boardId)`: if active+Running, recompute `ExpiresAt` via `PreviewLifecycle`, persist; return session or null.
- `StopAsync(tenantId, boardId)`: tear down + mark `Stopped` (persist), delete or keep record (keep record marked Stopped; reaper/Get ignore it).

Use injected `IdleMinutes`/`CapMinutes` (`PreviewSettings`) and `Func<DateTimeOffset>` clock (default `() => DateTimeOffset.UtcNow`) for testability. URL format: `https://{fqdn}:8080`.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class PreviewServiceTests
{
    sealed class FakeProvisioner : IPreviewProvisioner
    {
        public bool Fail; public List<string> Destroyed = new();
        public Task<PreviewProvisionResult> ProvisionAsync(GitHubRepoConnection r, string b, string? p, string dns, CancellationToken ct)
            => Fail ? throw new InvalidOperationException("boom")
                    : Task.FromResult(new PreviewProvisionResult($"{dns}.westeurope.azurecontainer.io", dns));
        public Task DestroyAsync(string name, CancellationToken ct) { Destroyed.Add(name); return Task.CompletedTask; }
        public Task<IReadOnlyList<PreviewGroupInfo>> ListPreviewGroupsAsync(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<PreviewGroupInfo>)new List<PreviewGroupInfo>());
    }

    static (PreviewService svc, InMemoryCosmosDbService cosmos, FakeProvisioner prov) Make(DateTimeOffset now)
    {
        var cosmos = new InMemoryCosmosDbService();
        cosmos.UpsertBoardAsync(new Board { Id = "b1", TenantId = "t1",
            GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", RepoUrl = "https://github.com/o/r", PatSecretName = "" } }).Wait();
        var prov = new FakeProvisioner();
        var svc = new PreviewService(cosmos, prov, new NullSecretProvider(),
            new PreviewSettings { IdleMinutes = 15, CapMinutes = 45 }, () => now);
        return (svc, cosmos, prov);
    }

    [Fact]
    public async Task Start_provisions_and_marks_running()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var (svc, _, _) = Make(now);
        var s = await svc.StartAsync("t1", "b1", "main", default);
        Assert.Equal(PreviewStatus.Running, s.Status);
        Assert.StartsWith("https://tpv-", s.Url);
        Assert.EndsWith(":8080", s.Url);
        Assert.Equal(now.AddMinutes(15), s.ExpiresAt);
    }

    [Fact]
    public async Task Start_replaces_existing_preview()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var (svc, _, prov) = Make(now);
        var first = await svc.StartAsync("t1", "b1", "main", default);
        var second = await svc.StartAsync("t1", "b1", "dev", default);
        Assert.Contains(first.ContainerName!, prov.Destroyed); // old torn down
        Assert.Equal("dev", second.Branch);
    }

    [Fact]
    public async Task Start_failure_marks_failed_with_error()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var (svc, _, prov) = Make(now); prov.Fail = true;
        var s = await svc.StartAsync("t1", "b1", "main", default);
        Assert.Equal(PreviewStatus.Failed, s.Status);
        Assert.False(string.IsNullOrEmpty(s.Error));
    }

    [Fact]
    public async Task Start_without_github_throws()
    {
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var cosmos = new InMemoryCosmosDbService();
        await cosmos.UpsertBoardAsync(new Board { Id = "b2", TenantId = "t1" });
        var svc = new PreviewService(cosmos, new FakeProvisioner(), new NullSecretProvider(),
            new PreviewSettings { IdleMinutes = 15, CapMinutes = 45 }, () => now);
        await Assert.ThrowsAsync<PreviewNotConnectedException>(() => svc.StartAsync("t1", "b2", "main", default));
    }

    [Fact]
    public async Task Heartbeat_extends_expiry()
    {
        var t0 = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var now = t0;
        var cosmos = new InMemoryCosmosDbService();
        await cosmos.UpsertBoardAsync(new Board { Id = "b1", TenantId = "t1",
            GitHub = new GitHubRepoConnection { Owner = "o", Repo = "r", RepoUrl = "https://github.com/o/r", PatSecretName = "" } });
        var svc = new PreviewService(cosmos, new FakeProvisioner(), new NullSecretProvider(),
            new PreviewSettings { IdleMinutes = 15, CapMinutes = 45 }, () => now);
        await svc.StartAsync("t1", "b1", "main", default);
        now = t0.AddMinutes(10);
        var s = await svc.HeartbeatAsync("t1", "b1", default);
        Assert.Equal(t0.AddMinutes(25), s!.ExpiresAt); // 10 + 15
    }
}
```

`NullSecretProvider` test helper (add to the test file if not present in tests project):

```csharp
sealed class NullSecretProvider : TectikaAgents.Core.Interfaces.ISecretProvider
{
    public Task<string> GetSecretAsync(string name, CancellationToken ct = default) => Task.FromResult("");
    public Task SetSecretAsync(string name, string value, CancellationToken ct = default) => Task.CompletedTask;
}
```

> Implementer: verify `ISecretProvider`'s exact signature and `InMemoryCosmosDbService`'s board-upsert
> method name (`UpsertBoardAsync` vs `CreateBoardAsync`); adjust the test to match.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~PreviewServiceTests"`
Expected: FAIL (PreviewService/PreviewSettings/PreviewNotConnectedException not defined).

- [ ] **Step 3: Implement the service**

`src/api/TectikaAgents.Api/Services/IPreviewService.cs`:

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public sealed class PreviewSettings { public int IdleMinutes { get; set; } = 15; public int CapMinutes { get; set; } = 45; }
public sealed class PreviewNotConnectedException : Exception { }

public interface IPreviewService
{
    Task<PreviewSession> StartAsync(string tenantId, string boardId, string branch, CancellationToken ct);
    Task<PreviewSession?> GetAsync(string tenantId, string boardId, CancellationToken ct);
    Task<PreviewSession?> HeartbeatAsync(string tenantId, string boardId, CancellationToken ct);
    Task StopAsync(string tenantId, string boardId, CancellationToken ct);
}
```

`src/api/TectikaAgents.Api/Services/PreviewService.cs`:

```csharp
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public sealed class PreviewService : IPreviewService
{
    private readonly ICosmosDbService _cosmos;
    private readonly IPreviewProvisioner _prov;
    private readonly ISecretProvider _secrets;
    private readonly PreviewSettings _settings;
    private readonly Func<DateTimeOffset> _now;

    public PreviewService(ICosmosDbService cosmos, IPreviewProvisioner prov, ISecretProvider secrets,
        PreviewSettings settings, Func<DateTimeOffset> now)
    { _cosmos = cosmos; _prov = prov; _secrets = secrets; _settings = settings; _now = now; }

    public async Task<PreviewSession> StartAsync(string tenantId, string boardId, string branch, CancellationToken ct)
    {
        var board = await _cosmos.GetBoardAsync(tenantId, boardId, ct)
            ?? throw new KeyNotFoundException("board");
        if (board.GitHub is null) throw new PreviewNotConnectedException();
        var repo = board.GitHub;
        repo.Repo = GitHubRepoConnection.NormalizeRepoName(repo.Repo);

        // Replace any existing active preview for this board.
        var existing = await _cosmos.GetPreviewAsync(boardId, ct);
        if (existing?.ContainerName is not null)
        {
            await _prov.DestroyAsync(existing.ContainerName, ct);
            existing.Status = PreviewStatus.Stopped;
            await _cosmos.UpsertPreviewAsync(existing, ct);
        }

        var now = _now();
        var s = new PreviewSession
        {
            Id = PreviewLifecycle.NewDnsLabel(), BoardId = boardId, TenantId = tenantId, Branch = branch,
            Status = PreviewStatus.Provisioning, CreatedAt = now, LastActivityAt = now,
            ExpiresAt = PreviewLifecycle.ComputeExpiry(now, now, _settings.IdleMinutes, _settings.CapMinutes),
        };
        await _cosmos.UpsertPreviewAsync(s, ct);

        try
        {
            var pat = string.IsNullOrEmpty(repo.PatSecretName) ? null
                : await _secrets.GetSecretAsync(repo.PatSecretName, ct);
            var r = await _prov.ProvisionAsync(repo, branch, pat, s.Id, ct);
            s.Status = PreviewStatus.Running;
            s.ContainerName = r.ContainerName;
            s.Url = $"https://{r.Fqdn}:8080";
        }
        catch (Exception ex)
        {
            s.Status = PreviewStatus.Failed;
            s.Error = ex.Message;
        }
        await _cosmos.UpsertPreviewAsync(s, ct);
        return s;
    }

    public Task<PreviewSession?> GetAsync(string tenantId, string boardId, CancellationToken ct)
        => _cosmos.GetPreviewAsync(boardId, ct);

    public async Task<PreviewSession?> HeartbeatAsync(string tenantId, string boardId, CancellationToken ct)
    {
        var s = await _cosmos.GetPreviewAsync(boardId, ct);
        if (s is null || s.Status != PreviewStatus.Running) return s;
        var now = _now();
        s.LastActivityAt = now;
        s.ExpiresAt = PreviewLifecycle.ComputeExpiry(s.CreatedAt, now, _settings.IdleMinutes, _settings.CapMinutes);
        await _cosmos.UpsertPreviewAsync(s, ct);
        return s;
    }

    public async Task StopAsync(string tenantId, string boardId, CancellationToken ct)
    {
        var s = await _cosmos.GetPreviewAsync(boardId, ct);
        if (s is null) return;
        if (s.ContainerName is not null) await _prov.DestroyAsync(s.ContainerName, ct);
        s.Status = PreviewStatus.Stopped;
        await _cosmos.UpsertPreviewAsync(s, ct);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~PreviewServiceTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/IPreviewService.cs src/api/TectikaAgents.Api/Services/PreviewService.cs tests/TectikaAgents.Tests/PreviewServiceTests.cs
git commit -m "feat(api): PreviewService orchestration + tests"
```

---

### Task 6: PreviewController

**Files:**
- Create: `src/api/TectikaAgents.Api/Controllers/PreviewController.cs`

Mirror `RepoController` for tenant scoping (`User.FindFirst("tid")`) and the connected guard.

- [ ] **Step 1: Write the controller**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;

namespace TectikaAgents.Api.Controllers;

public sealed record StartPreviewRequest(string Branch);

[ApiController]
[Route("api/boards/{boardId}/preview")]
[Authorize]
public class PreviewController : ControllerBase
{
    private readonly IPreviewService _preview;
    public PreviewController(IPreviewService preview) => _preview = preview;

    private string TenantId => User.FindFirst("tid")?.Value ?? "default";

    [HttpPost]
    public async Task<IActionResult> Start(string boardId, [FromBody] StartPreviewRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Branch)) return BadRequest(new { error = "branch is required" });
        try { return Ok(await _preview.StartAsync(TenantId, boardId, req.Branch, ct)); }
        catch (PreviewNotConnectedException) { return Conflict(new { error = "GitHubNotConnected" }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet]
    public async Task<IActionResult> Get(string boardId, CancellationToken ct)
    {
        var s = await _preview.GetAsync(TenantId, boardId, ct);
        return s is null ? NotFound() : Ok(s);
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(string boardId, CancellationToken ct)
    {
        var s = await _preview.HeartbeatAsync(TenantId, boardId, ct);
        return s is null ? NotFound() : Ok(s);
    }

    [HttpDelete]
    public async Task<IActionResult> Stop(string boardId, CancellationToken ct)
    {
        await _preview.StopAsync(TenantId, boardId, ct);
        return NoContent();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/PreviewController.cs
git commit -m "feat(api): PreviewController endpoints"
```

---

### Task 7: Idle reaper + DI wiring

**Files:**
- Create: `src/api/TectikaAgents.Api/Services/PreviewReaper.cs` (pure selector + `BackgroundService`)
- Modify: `src/api/TectikaAgents.Api/Program.cs`
- Test: `tests/TectikaAgents.Tests/PreviewReaperTests.cs`

- [ ] **Step 1: Write the failing test (pure selector)**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class PreviewReaperTests
{
    static readonly DateTimeOffset Now = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Selects_expired_sessions_for_teardown()
    {
        var sessions = new[]
        {
            new PreviewSession { Id = "a", ContainerName = "a", ExpiresAt = Now.AddMinutes(-1), Status = PreviewStatus.Running },
            new PreviewSession { Id = "b", ContainerName = "b", ExpiresAt = Now.AddMinutes(5),  Status = PreviewStatus.Running },
        };
        var expired = PreviewReaper.SelectExpired(sessions, Now).Select(s => s.Id).ToList();
        Assert.Equal(new[] { "a" }, expired);
    }

    [Fact]
    public void Selects_orphan_groups_not_in_active_set()
    {
        var active = new[] { new PreviewSession { ContainerName = "keep" } };
        var groups = new[] { new PreviewGroupInfo("keep", "b1"), new PreviewGroupInfo("orphan", "b2") };
        var orphans = PreviewReaper.SelectOrphans(groups, active).Select(g => g.Name).ToList();
        Assert.Equal(new[] { "orphan" }, orphans);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~PreviewReaperTests"`
Expected: FAIL (PreviewReaper not defined).

- [ ] **Step 3: Implement reaper**

`src/api/TectikaAgents.Api/Services/PreviewReaper.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Services;

public static class PreviewReaper
{
    public static IEnumerable<PreviewSession> SelectExpired(IEnumerable<PreviewSession> sessions, DateTimeOffset now)
        => sessions.Where(s => PreviewLifecycle.IsExpired(s, now));

    public static IEnumerable<PreviewGroupInfo> SelectOrphans(
        IEnumerable<PreviewGroupInfo> groups, IEnumerable<PreviewSession> active)
    {
        var live = active.Where(a => a.ContainerName is not null).Select(a => a.ContainerName!).ToHashSet();
        return groups.Where(g => !live.Contains(g.Name));
    }
}

public sealed class PreviewIdleReaperService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IPreviewProvisioner _prov;
    private readonly ILogger<PreviewIdleReaperService> _log;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    public PreviewIdleReaperService(IServiceProvider sp, IPreviewProvisioner prov, ILogger<PreviewIdleReaperService> log)
    { _sp = sp; _prov = prov; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var cosmos = scope.ServiceProvider.GetRequiredService<ICosmosDbService>();
                var active = await cosmos.ListActivePreviewsAsync(ct);
                var now = DateTimeOffset.UtcNow;
                foreach (var s in PreviewReaper.SelectExpired(active, now))
                {
                    if (s.ContainerName is not null) await _prov.DestroyAsync(s.ContainerName, ct);
                    s.Status = PreviewStatus.Stopped;
                    await cosmos.UpsertPreviewAsync(s, ct);
                    _log.LogInformation("[PreviewReaper] reaped expired {Id}", s.Id);
                }
                var groups = await _prov.ListPreviewGroupsAsync(ct);
                foreach (var g in PreviewReaper.SelectOrphans(groups, active))
                {
                    await _prov.DestroyAsync(g.Name, ct);
                    _log.LogInformation("[PreviewReaper] reaped orphan {Name}", g.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "[PreviewReaper] sweep failed");
            }
            await Task.Delay(Interval, ct);
        }
    }
}
```

- [ ] **Step 4: Wire DI in Program.cs**

After the GitHub read-service registration block, add:

```csharp
// ── Live Preview ────────────────────────────────────────────────────────────
builder.Services.AddSingleton(new PreviewSettings
{
    IdleMinutes = builder.Configuration.GetValue("Preview:IdleMinutes", 15),
    CapMinutes  = builder.Configuration.GetValue("Preview:CapMinutes", 45),
});
builder.Services.AddSingleton(new AciPreviewOptions
{
    ResourceGroup  = builder.Configuration["Preview:ResourceGroup"] ?? "rg-agentteam-dev-001",
    Region         = builder.Configuration["Preview:Region"] ?? "westeurope",
    AcrImage       = builder.Configuration["Preview:AcrImage"] ?? "tacragentteam.azurecr.io/preview-runner:latest",
    AcrLoginServer = builder.Configuration["Preview:AcrLoginServer"] ?? "tacragentteam.azurecr.io",
    MiResourceId   = builder.Configuration["Preview:MiResourceId"],
});
builder.Services.AddSingleton<IPreviewProvisioner, AciPreviewProvisioner>();
builder.Services.AddSingleton<Func<DateTimeOffset>>(_ => () => DateTimeOffset.UtcNow);
builder.Services.AddScoped<IPreviewService, PreviewService>();
builder.Services.AddHostedService<PreviewIdleReaperService>();
```

Add `using TectikaAgents.AgentRuntime.Preview;` and `using TectikaAgents.Core.Interfaces;` at the top of Program.cs if not present.

> NOTE: `PreviewService` is `Scoped` but takes singletons + `ICosmosDbService` (already registered).
> If `ICosmosDbService` is a singleton in this app, register `IPreviewService` as Singleton instead — match the
> lifetime of `ICosmosDbService` to avoid a captive-dependency warning.

- [ ] **Step 5: Run reaper tests + full build**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter "FullyQualifiedName~PreviewReaperTests"`
Expected: PASS (2 tests).
Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/PreviewReaper.cs src/api/TectikaAgents.Api/Program.cs tests/TectikaAgents.Tests/PreviewReaperTests.cs
git commit -m "feat(api): preview idle reaper + DI wiring"
```

---

## Phase B — Preview runner image

### Task 8: preview-runner Dockerfile + entrypoint

**Files:**
- Create: `docker/preview-runner/Dockerfile`
- Create: `docker/preview-runner/entrypoint.sh`

Reference: `docker/workspace-executor/entrypoint.sh` (clone/PAT/branch logic).

- [ ] **Step 1: Write entrypoint**

`docker/preview-runner/entrypoint.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

: "${REPO_URL:?REPO_URL required}"
: "${GIT_BRANCH:?GIT_BRANCH required}"

git config --global credential.helper store
printf "https://x-access-token:%s@github.com\n" "${GIT_PAT:-}" > /root/.git-credentials
git config --global user.email "preview@tectika.com"
git config --global user.name "Tectika Preview"

git clone "$REPO_URL" /app
cd /app
git checkout "$GIT_BRANCH"

export PORT=8080 HOST=0.0.0.0 HOSTNAME=0.0.0.0

# Install with the lockfile's package manager.
if [ -f pnpm-lock.yaml ]; then corepack enable && pnpm install --frozen-lockfile;
elif [ -f yarn.lock ]; then corepack enable && yarn install --frozen-lockfile;
else npm install; fi

# Prefer a dev script; fall back to start. Fail loudly if neither exists.
if npm run | grep -qE '^  dev'; then exec npm run dev;
elif npm run | grep -qE '^  start'; then exec npm start;
else echo "No 'dev' or 'start' script in package.json — not previewable" >&2; exit 1; fi
```

`docker/preview-runner/Dockerfile`:

```dockerfile
FROM node:20-slim
RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
```

- [ ] **Step 2: Lint the script**

Run: `bash -n docker/preview-runner/entrypoint.sh`
Expected: no output (syntax OK).

- [ ] **Step 3: Commit**

```bash
git add docker/preview-runner/Dockerfile docker/preview-runner/entrypoint.sh
git commit -m "feat(docker): preview-runner image (clone + install + serve on :8080)"
```

---

## Phase C — Frontend

### Task 9: Types + API client

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`
- Modify: `src/web/tectika-board/src/lib/api.ts`
- Test: `src/web/tectika-board/src/lib/__tests__/preview-api.test.ts`

- [ ] **Step 1: Add types (types.ts)**

```typescript
export type PreviewStatus = 'Provisioning' | 'Running' | 'Failed' | 'Stopped';

export interface PreviewSession {
  id: string;
  boardId: string;
  branch: string;
  status: PreviewStatus;
  url?: string;
  error?: string;
  createdAt: string;
  expiresAt: string;
}
```

- [ ] **Step 2: Add API client group (api.ts)**

Mirror the existing `api.repo.*` style (same base URL + auth header helper this file already uses):

```typescript
preview: {
  start: (boardId: string, branch: string) =>
    apiFetch<PreviewSession>(`/api/boards/${boardId}/preview`, {
      method: 'POST', body: JSON.stringify({ branch }),
    }),
  get: (boardId: string) =>
    apiFetch<PreviewSession | null>(`/api/boards/${boardId}/preview`, { method: 'GET', allow404: true }),
  heartbeat: (boardId: string) =>
    apiFetch<PreviewSession>(`/api/boards/${boardId}/preview/heartbeat`, { method: 'POST' }),
  stop: (boardId: string) =>
    apiFetch<void>(`/api/boards/${boardId}/preview`, { method: 'DELETE' }),
},
```

> Implementer: match the actual fetch helper name/signature in `api.ts` (e.g. `apiFetch`/`request`),
> how it injects the MSAL token, and how it handles 404 (the `get` poll must treat 404 as `null`).
> If there's no `allow404` option, add a `.get` variant that catches 404 → null, consistent with the file's conventions.

- [ ] **Step 3: Write the client test**

`src/web/tectika-board/src/lib/__tests__/preview-api.test.ts`:

```typescript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { api } from '../api.ts';

test('preview client builds correct routes', async () => {
  const calls: { url: string; method: string; body?: string }[] = [];
  const origFetch = globalThis.fetch;
  // @ts-expect-error test stub
  globalThis.fetch = async (url: string, init: RequestInit = {}) => {
    calls.push({ url: String(url), method: init.method ?? 'GET', body: init.body as string });
    return new Response(JSON.stringify({ id: 'tpv-x', boardId: 'b1', branch: 'main', status: 'Provisioning',
      createdAt: '', expiresAt: '' }), { status: 200, headers: { 'content-type': 'application/json' } });
  };
  try {
    await api.preview.start('b1', 'main');
    await api.preview.heartbeat('b1');
    await api.preview.stop('b1');
    assert.ok(calls[0].url.endsWith('/api/boards/b1/preview'));
    assert.equal(calls[0].method, 'POST');
    assert.match(calls[0].body!, /"branch":"main"/);
    assert.ok(calls[1].url.endsWith('/api/boards/b1/preview/heartbeat'));
    assert.equal(calls[2].method, 'DELETE');
  } finally { globalThis.fetch = origFetch; }
});
```

- [ ] **Step 4: Run the test**

Run: `cd src/web/tectika-board && node --test --experimental-strip-types src/lib/__tests__/preview-api.test.ts`
Expected: PASS (1 test). (Adjust the import/token-mock to whatever `api.ts` requires; if `api.ts` needs MSAL at import time, stub it like other tests in this repo do.)

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/lib/api.ts src/web/tectika-board/src/lib/__tests__/preview-api.test.ts
git commit -m "feat(web): preview types + api client"
```

---

### Task 10: PreviewTab + RepoView wiring

**Files:**
- Create: `src/web/tectika-board/src/components/board/repo/PreviewTab.tsx`
- Modify: `src/web/tectika-board/src/components/board/repo/RepoView.tsx`

- [ ] **Step 1: Write PreviewTab**

`src/web/tectika-board/src/components/board/repo/PreviewTab.tsx`:

```tsx
'use client';
import { useEffect, useRef, useState } from 'react';
import { api } from '@/lib/api';
import type { PreviewSession } from '@/lib/types';

export function PreviewTab({ boardId, branch }: { boardId: string; branch: string }) {
  const [session, setSession] = useState<PreviewSession | null>(null);
  const [busy, setBusy] = useState(false);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const beatRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => { api.preview.get(boardId).then(setSession).catch(() => setSession(null)); }, [boardId]);

  // Poll while provisioning.
  useEffect(() => {
    if (session?.status === 'Provisioning') {
      pollRef.current = setInterval(async () => {
        const s = await api.preview.get(boardId).catch(() => null);
        if (s) setSession(s);
      }, 2000);
      return () => { if (pollRef.current) clearInterval(pollRef.current); };
    }
  }, [session?.status, boardId]);

  // Heartbeat while running.
  useEffect(() => {
    if (session?.status === 'Running') {
      beatRef.current = setInterval(() => { api.preview.heartbeat(boardId).then(setSession).catch(() => {}); }, 60000);
      return () => { if (beatRef.current) clearInterval(beatRef.current); };
    }
  }, [session?.status, boardId]);

  const start = async () => { setBusy(true); try { setSession(await api.preview.start(boardId, branch)); } finally { setBusy(false); } };
  const stop  = async () => { setBusy(true); try { await api.preview.stop(boardId); setSession(null); } finally { setBusy(false); } };

  if (!session || session.status === 'Stopped')
    return (
      <div style={{ padding: 16 }}>
        <p>Preview branch <b>{branch}</b> as a running app.</p>
        <button disabled={busy} onClick={start}>▶ Start preview</button>
      </div>
    );
  if (session.status === 'Provisioning')
    return <div style={{ padding: 16 }}>⏳ Starting preview of <b>{session.branch}</b> — cloning &amp; installing…</div>;
  if (session.status === 'Failed')
    return (
      <div style={{ padding: 16 }}>
        <p>⚠️ Preview failed: {session.error}</p>
        <button disabled={busy} onClick={start}>Retry</button>
      </div>
    );
  // Running
  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', padding: '6px 10px', borderBottom: '1px solid #333' }}>
        <span>● live · {session.branch}</span>
        <a href={session.url} target="_blank" rel="noreferrer">Open ↗</a>
        <button onClick={() => navigator.clipboard.writeText(session.url ?? '')}>Copy link</button>
        <button style={{ marginLeft: 'auto' }} disabled={busy} onClick={stop}>■ Stop</button>
      </div>
      <iframe title="preview" src={session.url} style={{ flex: 1, border: 0, width: '100%' }} />
    </div>
  );
}
```

- [ ] **Step 2: Wire into RepoView.tsx**

1. Extend the `Sub` union: `type Sub = 'code' | 'history' | 'pulls' | 'changes' | 'preview';`
2. Add a tab button next to "Pull Requests":

```tsx
<button onClick={() => setSub('preview')}
  style={{ /* match sibling tab button styles */ fontWeight: sub === 'preview' ? 700 : 400 }}>
  Preview
</button>
```

3. Add the render branch alongside the others:

```tsx
{sub === 'preview' && <PreviewTab boardId={boardId} branch={branch} />}
```

4. Add the import: `import { PreviewTab } from './PreviewTab';`

- [ ] **Step 3: Typecheck + build**

Run: `cd src/web/tectika-board && npx tsc --noEmit`
Expected: no errors in the new/changed files.
Run: `cd src/web/tectika-board && npm run build`
Expected: build succeeds (per AGENTS.md Turbopack notes).

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/components/board/repo/PreviewTab.tsx src/web/tectika-board/src/components/board/repo/RepoView.tsx
git commit -m "feat(web): Preview sub-tab in Repo view"
```

---

## Phase D — Infra & deploy

### Task 11: deploy.sh / deploy.ps1 — build preview-runner

**Files:**
- Modify: `scripts/deploy.sh`
- Modify: `scripts/deploy.ps1`

- [ ] **Step 1: Add an image build to deploy_api (or a new `--preview-image` step)**

In `scripts/deploy.sh`, add a config default near the other images:

```bash
PREVIEW_IMAGE="${TECTIKA_PREVIEW_IMAGE:-preview-runner}"
```

And in `deploy_api()` (so the preview image ships with the API that uses it), before/after the API build add:

```bash
log "Building preview-runner image"
az acr build -r "$ACR_NAME" \
  -t "${PREVIEW_IMAGE}:${SHA}" -t "${PREVIEW_IMAGE}:latest" \
  -f docker/preview-runner/Dockerfile docker/preview-runner/
info "preview-runner image ${PREVIEW_IMAGE}:${SHA} pushed."
```

(Context is `docker/preview-runner/` because the Dockerfile `COPY entrypoint.sh` expects that dir as context.)

- [ ] **Step 2: Mirror in deploy.ps1**

Add the equivalent PowerShell lines in the API deploy function (same `az acr build` invocation).

- [ ] **Step 3: Commit**

```bash
git add scripts/deploy.sh scripts/deploy.ps1
git commit -m "build(deploy): build + push preview-runner image with the API"
```

---

### Task 12: Infra bicep — Cosmos container, API-MI ACI role, app settings

**Files:**
- Modify: `infra/modules/data.bicep` (Cosmos container) — or wherever containers are declared.
- Modify: `infra/modules/containerapps.bicep` (API app settings + role assignment) — or the relevant module.
- Modify: `infra/main.bicep` if parameters/wiring are needed.

- [ ] **Step 1: Add the Cosmos container**

In the module that declares Cosmos SQL containers, add `previewSessions` with partition key `/boardId`, mirroring an existing container resource (e.g. `tasks` with `/boardId`).

- [ ] **Step 2: Grant the API managed identity ACI rights**

Add a `Microsoft.Authorization/roleAssignments` granting the **API** user-assigned identity the **Contributor** role (or a custom role limited to `Microsoft.ContainerInstance/*`) scoped to the resource group — mirror how the Workflows identity gets its ACI permission today.

- [ ] **Step 3: Add API app settings**

Add to the API container app env/app-settings:

```
Preview__ResourceGroup   = <rg name>
Preview__Region          = westeurope
Preview__AcrImage        = <acr login server>/preview-runner:latest
Preview__AcrLoginServer  = <acr login server>
Preview__MiResourceId    = <the UAMI resource id used for ACR pull>
Preview__IdleMinutes     = 15
Preview__CapMinutes      = 45
```

(`__` is the .NET config nesting separator for env vars → maps to `Preview:MiResourceId` etc.)

- [ ] **Step 4: Manual deploy note (record in commit body)**

Because `EnsureInfrastructureAsync` swallows container-create failures, after `infra` deploy also run:

```bash
az cosmosdb sql container create -a <cosmos-acct> -g <rg> -d <db> -n previewSessions --partition-key-path /boardId
```

- [ ] **Step 5: Commit**

```bash
git add infra/
git commit -m "infra: previewSessions container, API-MI ACI role, preview app settings"
```

---

## Final verification

- [ ] Run the full backend suite: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` → all pass.
- [ ] Run the frontend preview test: `cd src/web/tectika-board && node --test --experimental-strip-types src/lib/__tests__/preview-api.test.ts` → pass.
- [ ] Frontend build: `cd src/web/tectika-board && npm run build` → succeeds.
- [ ] Manual (post-deploy): connect a board to a Next.js repo, open Repo → Preview → Start; confirm Provisioning → Running, the iframe shows the app, Stop tears it down, and an idle preview disappears within ~16 min.

## Self-review notes (coverage)

- Scope "runnable web apps only" → runner fails loudly when no `dev`/`start` (Task 8); UI shows Failed (Task 10).
- On-demand + auto-idle → Start endpoint (Task 6) + heartbeat + reaper (Task 5/7).
- Board Repo view, any branch → PreviewTab uses RepoView's selected `branch` (Task 10).
- Public unguessable URL → `NewDnsLabel` (Task 1), public IP + DnsNameLabel (Task 3).
- One preview per board / replace → `StartAsync` tears down existing (Task 5).
- Security: PAT as SecureValue (Task 3), tenant scoping (Task 6), 409 not-connected (Task 5/6).
- Infra idempotency → Task 12 (+ manual container note).
```
