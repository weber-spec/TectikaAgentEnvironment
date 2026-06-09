# Phase 1 — Real Azure AI Foundry Agent Service agents — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single-shot chat-completion shim with real Azure AI Foundry Agent Service agents — one persistent agent per role (created eagerly from the Agents tab), a persistent thread per task, a real server-side run, behind one `IAgentRuntime`/`IAgentProvisioner` abstraction — with full mock-mode parity.

**Architecture:** Interfaces + DTOs live in `TectikaAgents.Core` (no SDK dependency). A new `TectikaAgents.AgentRuntime` class library isolates the beta `Azure.AI.Agents.Persistent` SDK and holds `FoundryAgentRuntime` + `MockAgentRuntime`/`MockAgentProvisioner`; it is referenced by both the API and the Workflows projects. The API's `AgentRolesController` provisions agents eagerly; the Workflows `InvokeAgentActivity` runs turns. The two old runners are deleted.

**Tech Stack:** .NET 10, C#; Azure.AI.Agents.Persistent (beta, pinned); Azure Durable Functions (isolated worker); xUnit (new test project); Bicep (infra); Next.js (light FE touch).

---

## Testing strategy

- **Unit tests (xUnit, new `tests/TectikaAgents.Tests` project):** the pure/mockable logic — `MockAgentProvisioner`/`MockAgentRuntime`, `ContextManager.BuildUserContentAsync`, the agent change-detection hash, and `RunsController` pipeline-from-assignee derivation.
- **Mock E2E (no Azure):** run API + Workflows with `Foundry:UseMock=true`; drive create-agent → assign → run → artifact → SSE. Scripted with `curl`.
- **Azure smoke (after mock):** one real agent + thread + turn against the live Foundry project. This is the only verification for the real `FoundryAgentRuntime` (it cannot be unit-tested without Azure).

Run all unit tests with: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`

---

## File structure

**New:**
- `src/agentruntime/TectikaAgents.AgentRuntime.csproj` — class library (beta SDK isolated here)
- `src/agentruntime/FoundryAgentRuntime.cs` — real impl of both interfaces
- `src/agentruntime/MockAgentRuntime.cs` — mock `IAgentRuntime` + `MockAgentProvisioner`
- `src/agentruntime/AgentInstructionsHash.cs` — pure change-detection helper
- `src/core/TectikaAgents.Core/Interfaces/IAgentProvisioner.cs`
- `src/core/TectikaAgents.Core/Interfaces/IAgentRuntime.cs`
- `src/core/TectikaAgents.Core/Models/AgentRunContracts.cs` — `AgentRunRequest`, `AgentRunOutcome`, `AgentRunStatus`, `AgentSyncResult`
- `tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj` + test files

**Modified:**
- `src/core/TectikaAgents.Core/Configuration/AppSettings.cs` — `FoundrySettings += ProjectEndpoint, MaxCompletionTokens`
- `src/api/TectikaAgents.Api/Controllers/AgentRolesController.cs` — eager provision on Upsert/Delete
- `src/api/TectikaAgents.Api/Controllers/RunsController.cs` — derive pipeline from `task.Assignee`
- `src/api/TectikaAgents.Api/Program.cs` — register `IAgentProvisioner`; drop `FoundryAgentService`
- `src/workflows/Activities/InvokeAgentActivity.cs` — use `IAgentRuntime` + `EnsureThreadAsync`
- `src/workflows/Services/ContextManager.cs` — add `BuildUserContentAsync`
- `src/workflows/Program.cs` — register `IAgentRuntime` (real/mock); drop `WorkflowAgentRunner`
- `src/web/tectika-board/src/app/agents/page.tsx` + `src/lib/api.ts`/`types.ts` — synced indicator
- `infra/modules/foundry.bicep`, `infra/modules/containerapps.bicep`, `infra/modules/functionapp.bicep`, `infra/deploy.ps1`
- `TectikaAgents.slnx` — add the two new projects

**Deleted:**
- `src/api/TectikaAgents.Api/Services/FoundryAgentService.cs`
- `src/workflows/Services/WorkflowAgentRunner.cs`

---

## Task 1: Scaffold the AgentRuntime library + test project

**Files:**
- Create: `src/agentruntime/TectikaAgents.AgentRuntime.csproj`
- Create: `tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
- Modify: `TectikaAgents.slnx`

- [ ] **Step 1: Create the class library project file**

`src/agentruntime/TectikaAgents.AgentRuntime.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../core/TectikaAgents.Core/TectikaAgents.Core.csproj" />
    <PackageReference Include="Azure.AI.Agents.Persistent" Version="1.1.0-beta.4" />
    <PackageReference Include="Azure.Identity" Version="1.13.1" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the test project file**

`tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/core/TectikaAgents.Core/TectikaAgents.Core.csproj" />
    <ProjectReference Include="../../src/agentruntime/TectikaAgents.AgentRuntime.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Register both projects in the solution**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment
dotnet sln TectikaAgents.slnx add src/agentruntime/TectikaAgents.AgentRuntime.csproj tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj
```
Expected: "Project ... added to the solution." twice.

- [ ] **Step 4: Restore to confirm the beta package resolves**

Run: `dotnet restore src/agentruntime/TectikaAgents.AgentRuntime.csproj`
Expected: restore succeeds. If `Azure.AI.Agents.Persistent 1.1.0-beta.4` is not found, run `dotnet add src/agentruntime package Azure.AI.Agents.Persistent --prerelease` and record the resolved version, then update the csproj to pin it.

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/TectikaAgents.AgentRuntime.csproj tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj TectikaAgents.slnx
git commit -m "build: scaffold AgentRuntime library + xUnit test project"
```

---

## Task 2: Core contracts (DTOs + interfaces)

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/AgentRunContracts.cs`
- Create: `src/core/TectikaAgents.Core/Interfaces/IAgentProvisioner.cs`
- Create: `src/core/TectikaAgents.Core/Interfaces/IAgentRuntime.cs`

- [ ] **Step 1: Create the DTOs**

`src/core/TectikaAgents.Core/Models/AgentRunContracts.cs`:
```csharp
namespace TectikaAgents.Core.Models;

/// <summary>Terminal outcome of one agent turn. RequiresApproval is reserved for a later phase.</summary>
public enum AgentRunStatus { Completed, Failed, BudgetExceeded, RequiresApproval }

/// <summary>Everything an agent turn needs. UserMessage is the assembled context (no system role).</summary>
public sealed record AgentRunRequest(
    AgentRole Role,
    AgentTask Task,
    string ThreadId,
    string UserMessage,
    int MaxCompletionTokens,
    string RunId,
    int Step);

public sealed record AgentRunOutcome(
    AgentRunStatus Status,
    string Content,
    ArtifactContentType ContentType,
    TokenUsage TokenUsage,
    string CompletionId,
    string? BriefUpdate = null,
    string? Error = null);

/// <summary>Result of ensuring a role's Foundry agent exists/updated.</summary>
public sealed record AgentSyncResult(string? FoundryAgentId, bool Synced, string? Error = null);
```

- [ ] **Step 2: Create `IAgentProvisioner`**

`src/core/TectikaAgents.Core/Interfaces/IAgentProvisioner.cs`:
```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Manages the lifecycle of a Foundry agent for a role (used by the Agents tab).</summary>
public interface IAgentProvisioner
{
    /// <summary>Create the agent if absent, update it if prompt/model changed. Returns the agent id + sync state.</summary>
    Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default);

    /// <summary>Delete the Foundry agent. No-op if id is null/empty or already gone.</summary>
    Task DeleteAgentAsync(string? foundryAgentId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Create `IAgentRuntime`**

`src/core/TectikaAgents.Core/Interfaces/IAgentRuntime.cs`:
```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Interfaces;

/// <summary>Runs agent turns against Foundry threads (used by the Workflows activity).</summary>
public interface IAgentRuntime
{
    /// <summary>Return the task's thread id, creating + persisting one if missing.</summary>
    Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default);

    /// <summary>Run one server-side turn and return its terminal outcome.</summary>
    Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, CancellationToken ct = default);
}
```

- [ ] **Step 4: Build core**

Run: `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/AgentRunContracts.cs src/core/TectikaAgents.Core/Interfaces/
git commit -m "feat(core): IAgentRuntime/IAgentProvisioner interfaces + run contracts"
```

---

## Task 3: Agent instructions change-detection hash (pure helper, TDD)

A role's Foundry agent must be updated only when its prompt/model actually changed. We hash `(systemPrompt, model)` and compare.

**Files:**
- Create: `src/agentruntime/AgentInstructionsHash.cs`
- Test: `tests/TectikaAgents.Tests/AgentInstructionsHashTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/TectikaAgents.Tests/AgentInstructionsHashTests.cs`:
```csharp
using TectikaAgents.AgentRuntime;
using Xunit;

public class AgentInstructionsHashTests
{
    [Fact]
    public void SameInputsProduceSameHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o");
        var b = AgentInstructionsHash.Compute("be helpful", "gpt-4o");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentPromptProducesDifferentHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o");
        var b = AgentInstructionsHash.Compute("be terse", "gpt-4o");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentModelProducesDifferentHash()
    {
        var a = AgentInstructionsHash.Compute("be helpful", "gpt-4o");
        var b = AgentInstructionsHash.Compute("be helpful", "gpt-4o-mini");
        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 2: Run the test, verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter AgentInstructionsHashTests`
Expected: FAIL (compile error — `AgentInstructionsHash` does not exist).

- [ ] **Step 3: Implement the helper**

`src/agentruntime/AgentInstructionsHash.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;

namespace TectikaAgents.AgentRuntime;

/// <summary>Deterministic hash of the agent-defining fields, to detect when a Foundry agent needs updating.</summary>
public static class AgentInstructionsHash
{
    public static string Compute(string systemPrompt, string model)
    {
        var bytes = Encoding.UTF8.GetBytes($"{model}\n \n{systemPrompt}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
```

- [ ] **Step 4: Run the test, verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter AgentInstructionsHashTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/AgentInstructionsHash.cs tests/TectikaAgents.Tests/AgentInstructionsHashTests.cs
git commit -m "feat(agentruntime): agent instructions change-detection hash"
```

---

## Task 4: Store the agent hash on AgentRole

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/AgentRole.cs`

- [ ] **Step 1: Add the field**

In `src/core/TectikaAgents.Core/Models/AgentRole.cs`, next to `FoundryAgentId`, add:
```csharp
    /// <summary>SHA-256 of (systemPrompt, model) at last successful Foundry sync. Null until first sync.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("foundryAgentHash")]
    public string? FoundryAgentHash { get; set; }
```

- [ ] **Step 2: Build core**

Run: `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/AgentRole.cs
git commit -m "feat(core): AgentRole.FoundryAgentHash for sync change-detection"
```

---

## Task 5: Mock provisioner + mock runtime (TDD)

The mock lets the whole flow run with no Azure. It must emit `agent_thinking`/`step_*` events; we model event emission behind a tiny callback so the mock has no Service Bus dependency.

**Files:**
- Create: `src/agentruntime/MockAgentRuntime.cs`
- Test: `tests/TectikaAgents.Tests/MockAgentRuntimeTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/TectikaAgents.Tests/MockAgentRuntimeTests.cs`:
```csharp
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

public class MockAgentRuntimeTests
{
    private static AgentRole Role() => new() { Id = "role-dev", DisplayName = "Dev", SystemPrompt = "code well", ModelOverride = "gpt-4o" };
    private static AgentTask Task() => new() { Id = "task-1", BoardId = "board-1", Title = "Do it" };

    [Fact]
    public async Task EnsureAgent_AssignsIdAndMarksSynced()
    {
        var p = new MockAgentProvisioner();
        var result = await p.EnsureAgentAsync(Role());
        Assert.True(result.Synced);
        Assert.False(string.IsNullOrEmpty(result.FoundryAgentId));
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task EnsureThread_ReturnsStableIdForSameTask()
    {
        var rt = new MockAgentRuntime();
        var t = Task();
        var id1 = await rt.EnsureThreadAsync(t);
        var id2 = await rt.EnsureThreadAsync(t);
        Assert.False(string.IsNullOrEmpty(id1));
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task RunTurn_ReturnsCompletedWithDeterministicContentAndUsage()
    {
        var rt = new MockAgentRuntime();
        var req = new AgentRunRequest(Role(), Task(), "thread-1", "## Task: Do it", 4096, "run-123456", 0);
        var outcome = await rt.RunTurnAsync(req);
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Contains("Do it", outcome.Content);
        Assert.True(outcome.TokenUsage.Input > 0);
        Assert.True(outcome.TokenUsage.Output > 0);
    }
}
```

- [ ] **Step 2: Run the tests, verify they fail**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter MockAgentRuntimeTests`
Expected: FAIL (compile error — `MockAgentProvisioner`/`MockAgentRuntime` do not exist).

- [ ] **Step 3: Implement the mocks**

`src/agentruntime/MockAgentRuntime.cs`:
```csharp
using System.Collections.Concurrent;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Deterministic, no-Azure provisioner. Returns a fake agent id and always "synced".</summary>
public sealed class MockAgentProvisioner : IAgentProvisioner
{
    public Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default)
        => Task.FromResult(new AgentSyncResult(
            FoundryAgentId: string.IsNullOrEmpty(role.FoundryAgentId) ? $"mock-agent-{role.Id}" : role.FoundryAgentId,
            Synced: true));

    public Task DeleteAgentAsync(string? foundryAgentId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Deterministic, no-Azure runtime. Stable thread per task; echoes the task into the artifact.</summary>
public sealed class MockAgentRuntime : IAgentRuntime
{
    private readonly ConcurrentDictionary<string, string> _threads = new();

    public Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(task.FoundryThreadId)) return Task.FromResult(task.FoundryThreadId!);
        var id = _threads.GetOrAdd(task.Id, _ => $"mock-thread-{task.Id}");
        return Task.FromResult(id);
    }

    public Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, CancellationToken ct = default)
    {
        var content =
            $"# {req.Role.DisplayName} output for: {req.Task.Title}\n\n" +
            $"(mock) Completed step {req.Step}.\n\n" +
            "## Brief Update\nMock turn complete.\n";
        var usage = new TokenUsage { Input = Math.Max(1, req.UserMessage.Length / 4), Output = Math.Max(1, content.Length / 4) };
        return Task.FromResult(new AgentRunOutcome(
            Status: AgentRunStatus.Completed,
            Content: content,
            ContentType: ArtifactContentType.Markdown,
            TokenUsage: usage,
            CompletionId: $"mock-cmpl-{req.RunId}-{req.Step}",
            BriefUpdate: "Mock turn complete."));
    }
}
```

- [ ] **Step 4: Run the tests, verify they pass**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter MockAgentRuntimeTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/agentruntime/MockAgentRuntime.cs tests/TectikaAgents.Tests/MockAgentRuntimeTests.cs
git commit -m "feat(agentruntime): mock provisioner + runtime (no-Azure parity)"
```

---

## Task 6: Config — FoundrySettings additions

**Files:**
- Modify: `src/core/TectikaAgents.Core/Configuration/AppSettings.cs`

- [ ] **Step 1: Add the fields to `FoundrySettings`**

In `src/core/TectikaAgents.Core/Configuration/AppSettings.cs`, inside `FoundrySettings`, add:
```csharp
    /// <summary>Foundry project (data-plane) endpoint for the Agent Service SDK:
    /// https://&lt;subdomain&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</summary>
    public string ProjectEndpoint { get; set; } = string.Empty;

    /// <summary>Per-turn output token cap passed to the Foundry run.</summary>
    public int MaxCompletionTokens { get; set; } = 4096;

    /// <summary>When true, use the no-Azure mock runtime/provisioner. Defaults to MockDatabase:Enabled in DI.</summary>
    public bool UseMock { get; set; }
```

- [ ] **Step 2: Build core**

Run: `dotnet build src/core/TectikaAgents.Core/TectikaAgents.Core.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/core/TectikaAgents.Core/Configuration/AppSettings.cs
git commit -m "feat(core): FoundrySettings.ProjectEndpoint/MaxCompletionTokens/UseMock"
```

---

## Task 7: SDK spike — lock the Azure.AI.Agents.Persistent surface

The beta SDK's type/method names drift between versions. Before writing `FoundryAgentRuntime`, confirm the exact surface for the pinned version so Task 8's code compiles.

**Files:** none committed (throwaway verification).

- [ ] **Step 1: Inspect the installed package surface**

Run:
```bash
cd /home/elimeshi/projects/repos/TectikaAgentEnvironment
dotnet build src/agentruntime/TectikaAgents.AgentRuntime.csproj 2>&1 | head -5
find ~/.nuget/packages/azure.ai.agents.persistent -name '*.xml' | head
```
Confirm these types/members exist for the pinned version (names as used in Task 8). If any differ, **adjust Task 8's code to match** before implementing:
- `PersistentAgentsClient(string endpoint, TokenCredential credential)`
- `client.Administration.CreateAgent(model, name, instructions)` → returns something with `.Id`
- `client.Administration.UpdateAgent(assistantId, model:, instructions:)`
- `client.Administration.DeleteAgent(assistantId)`
- `client.Threads.CreateThread()` → `.Id`
- `client.Messages.CreateMessage(threadId, MessageRole.User, content)`
- `client.Runs.CreateRunStreaming(threadId, assistantId, maxCompletionTokens:)` → stream of updates with text-delta + run-status + usage
- non-streaming fallback: `client.Runs.CreateRun(...)`, `client.Runs.GetRun(threadId, runId)`, `client.Messages.GetMessages(threadId)`

- [ ] **Step 2: Record findings**

Note the confirmed namespaces and any signature differences as a comment block at the top of `FoundryAgentRuntime.cs` in Task 8. (No commit for this task.)

---

## Task 8: FoundryAgentRuntime (real impl)

Cannot be unit-tested without Azure — verified by the Azure smoke test (Task 15). Implement carefully against the surface confirmed in Task 7. Event emission is abstracted behind an `Action<string,string>?` callback (`onText`, `onStatus`) injected by the caller (the Workflows activity wires it to the event publisher), so this library stays free of Service Bus.

**Files:**
- Create: `src/agentruntime/FoundryAgentRuntime.cs`

- [ ] **Step 1: Implement**

`src/agentruntime/FoundryAgentRuntime.cs` (adjust member names per Task 7 findings):
```csharp
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Interfaces;
using TectikaAgents.Core.Models;

namespace TectikaAgents.AgentRuntime;

/// <summary>Real Azure AI Foundry Agent Service runtime + provisioner. Persists agent/thread ids
/// onto the role/task objects passed in; the caller is responsible for saving them to Cosmos.</summary>
public sealed class FoundryAgentRuntime : IAgentRuntime, IAgentProvisioner
{
    private readonly PersistentAgentsClient _client;
    private readonly FoundrySettings _settings;
    private readonly ILogger<FoundryAgentRuntime> _logger;

    /// <summary>Optional per-turn streaming sink: onText(delta), onStatus(status). Set by the caller per run.</summary>
    public Action<string>? OnText { get; set; }
    public Action<string>? OnStatus { get; set; }

    public FoundryAgentRuntime(IOptions<FoundrySettings> settings, ILogger<FoundryAgentRuntime> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client = new PersistentAgentsClient(_settings.ProjectEndpoint, new DefaultAzureCredential());
    }

    public async Task<AgentSyncResult> EnsureAgentAsync(AgentRole role, CancellationToken ct = default)
    {
        try
        {
            var model = role.ModelOverride ?? _settings.DefaultModel;
            var hash = AgentInstructionsHash.Compute(role.SystemPrompt, model);

            if (string.IsNullOrEmpty(role.FoundryAgentId))
            {
                var created = await _client.Administration.CreateAgentAsync(
                    model: model, name: role.DisplayName, instructions: role.SystemPrompt, cancellationToken: ct);
                role.FoundryAgentId = created.Value.Id;
                role.FoundryAgentHash = hash;
                return new AgentSyncResult(role.FoundryAgentId, Synced: true);
            }

            if (role.FoundryAgentHash != hash)
            {
                await _client.Administration.UpdateAgentAsync(
                    role.FoundryAgentId, model: model, instructions: role.SystemPrompt, cancellationToken: ct);
                role.FoundryAgentHash = hash;
            }
            return new AgentSyncResult(role.FoundryAgentId, Synced: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EnsureAgent failed for role {Role}", role.Id);
            return new AgentSyncResult(role.FoundryAgentId, Synced: false, Error: ex.Message);
        }
    }

    public async Task DeleteAgentAsync(string? foundryAgentId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(foundryAgentId)) return;
        try { await _client.Administration.DeleteAgentAsync(foundryAgentId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteAgent failed (ignored) for {Id}", foundryAgentId); }
    }

    public async Task<string> EnsureThreadAsync(AgentTask task, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(task.FoundryThreadId)) return task.FoundryThreadId!;
        var thread = await _client.Threads.CreateThreadAsync(cancellationToken: ct);
        task.FoundryThreadId = thread.Value.Id;
        return task.FoundryThreadId!;
    }

    public async Task<AgentRunOutcome> RunTurnAsync(AgentRunRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(req.Role.FoundryAgentId))
            return Fail(req, "Role has no FoundryAgentId — ensure the agent first.");

        await _client.Messages.CreateMessageAsync(req.ThreadId, MessageRole.User, req.UserMessage, cancellationToken: ct);

        var text = new System.Text.StringBuilder();
        int input = 0, output = 0;
        string status = "completed";
        string completionId = $"run-{req.RunId}-{req.Step}";

        var stream = _client.Runs.CreateRunStreamingAsync(
            req.ThreadId, req.Role.FoundryAgentId, maxCompletionTokens: req.MaxCompletionTokens, cancellationToken: ct);

        await foreach (var update in stream)
        {
            switch (update)
            {
                case MessageContentUpdate mc when !string.IsNullOrEmpty(mc.Text):
                    text.Append(mc.Text);
                    OnText?.Invoke(mc.Text);
                    break;
                case RunUpdate ru:
                    completionId = ru.Value.Id;
                    status = ru.Value.Status.ToString().ToLowerInvariant();
                    OnStatus?.Invoke(status);
                    if (ru.Value.Usage is not null)
                    {
                        input = ru.Value.Usage.PromptTokens;
                        output = ru.Value.Usage.CompletionTokens;
                    }
                    break;
            }
        }

        if (status is "failed" or "cancelled" or "expired")
            return Fail(req, $"Foundry run ended with status '{status}'.");
        if (status == "incomplete")
            return new AgentRunOutcome(AgentRunStatus.BudgetExceeded, text.ToString(),
                ArtifactContentType.Markdown, new TokenUsage { Input = input, Output = output }, completionId);

        var content = text.ToString();
        return new AgentRunOutcome(
            AgentRunStatus.Completed, content, DetectType(content, req.Role),
            new TokenUsage { Input = input, Output = output }, completionId);
    }

    private static AgentRunOutcome Fail(AgentRunRequest req, string error) =>
        new(AgentRunStatus.Failed, "", ArtifactContentType.Markdown, new TokenUsage(),
            $"run-{req.RunId}-{req.Step}", Error: error);

    private static ArtifactContentType DetectType(string content, AgentRole role)
    {
        if (role.Id.Contains("backend") || role.Id.Contains("devops") || role.Id.Contains("qa"))
            return ArtifactContentType.Code;
        var t = content.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('[')) return ArtifactContentType.Json;
        return ArtifactContentType.Markdown;
    }
}
```

- [ ] **Step 2: Build the library**

Run: `dotnet build src/agentruntime/TectikaAgents.AgentRuntime.csproj`
Expected: Build succeeded. If member names differ from Task 7 findings, fix them now until it builds.

- [ ] **Step 3: Commit**

```bash
git add src/agentruntime/FoundryAgentRuntime.cs
git commit -m "feat(agentruntime): real FoundryAgentRuntime on Azure.AI.Agents.Persistent"
```

---

## Task 9: ContextManager.BuildUserContentAsync (TDD)

The agent now carries the system prompt as instructions, so the turn needs only the **user content** string (no system message).

**Files:**
- Modify: `src/workflows/Services/ContextManager.cs`
- Test: `tests/TectikaAgents.Tests/ContextManagerTests.cs` (only if `ContextManager` has no Cosmos dependency in its constructor for the assembly path; otherwise extract a pure `static string Assemble(...)` and test that)

- [ ] **Step 1: Read the current ContextManager**

Run: `sed -n '1,160p' src/workflows/Services/ContextManager.cs`
Identify the existing `BuildContextAsync` body that assembles the messages list (system + user). The user-content assembly (task title/description + upstream artifacts + TaskBrief + board goal) is what we extract.

- [ ] **Step 2: Write the failing test**

`tests/TectikaAgents.Tests/ContextManagerTests.cs`:
```csharp
using TectikaAgents.Core.Models;
using TectikaAgents.Workflows.Services;
using Xunit;

public class ContextManagerTests
{
    [Fact]
    public void Assemble_IncludesTaskAndUpstreamAndBrief_NoSystemPrompt()
    {
        var role = new AgentRole { Id = "r", DisplayName = "Dev", SystemPrompt = "SECRET-SYS-PROMPT" };
        var task = new AgentTask { Id = "t", Title = "Build X", Description = "do X", TaskBrief = "prior note" };
        var board = new Board { Id = "b" };
        var upstream = new List<Artifact> { new() { TaskId = "u", ContentType = ArtifactContentType.Markdown, Content = "UPSTREAM-DATA" } };

        var text = ContextManager.Assemble(role, task, board, upstream);

        Assert.Contains("Build X", text);
        Assert.Contains("UPSTREAM-DATA", text);
        Assert.Contains("prior note", text);
        Assert.DoesNotContain("SECRET-SYS-PROMPT", text); // system prompt lives on the agent, not here
    }
}
```
(Confirmed: `Board` has **no** `Goal`/`MasterPlan` fields yet — those arrive in Phase 2 — so the assembler must not reference them.)

- [ ] **Step 3: Run the test, verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter ContextManagerTests`
Expected: FAIL (compile error — `ContextManager.Assemble` does not exist). *(Add a ProjectReference to the workflows project in the test csproj if not present: `dotnet add tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj reference src/workflows/TectikaAgents.Workflows.csproj`.)*

- [ ] **Step 4: Implement**

In `src/workflows/Services/ContextManager.cs` add a pure static assembler and an instance wrapper:
```csharp
    /// <summary>Assemble the user-content string for an agent turn (no system prompt).
    /// Board.Goal/MasterPlan are added in Phase 2; not referenced here.</summary>
    public static string Assemble(AgentRole role, AgentTask task, Board board, IReadOnlyList<Artifact> upstream)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Task: {task.Title}");
        if (!string.IsNullOrWhiteSpace(task.Description)) sb.AppendLine(task.Description);
        if (!string.IsNullOrWhiteSpace(task.TaskBrief)) sb.AppendLine($"\n## Task brief (history)\n{task.TaskBrief}");
        foreach (var art in upstream)
        {
            sb.AppendLine($"\n### Input ({art.ContentType}):");
            sb.AppendLine("```");
            sb.AppendLine(art.Content);
            sb.AppendLine("```");
        }
        sb.AppendLine("\nComplete the task. Be thorough and production-ready.");
        sb.AppendLine("End with a one-line `## Brief Update`.");
        return sb.ToString();
    }

    /// <summary>Instance entry point used by the activity (kept async for future retrieval/summarize steps).</summary>
    public Task<string> BuildUserContentAsync(AgentRole role, AgentTask task, Board board,
        IReadOnlyList<Artifact> upstream, CancellationToken ct = default)
        => Task.FromResult(Assemble(role, task, board, upstream));
```

- [ ] **Step 5: Run the test, verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter ContextManagerTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/workflows/Services/ContextManager.cs tests/TectikaAgents.Tests/ContextManagerTests.cs tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj
git commit -m "feat(workflows): ContextManager.BuildUserContentAsync (no system prompt)"
```

---

## Task 10: Wire IAgentRuntime into InvokeAgentActivity; delete WorkflowAgentRunner

**Files:**
- Modify: `src/workflows/Activities/InvokeAgentActivity.cs`
- Modify: `src/workflows/Program.cs`
- Delete: `src/workflows/Services/WorkflowAgentRunner.cs`

- [ ] **Step 1: Replace the runner with the runtime in the activity**

In `src/workflows/Activities/InvokeAgentActivity.cs`:
- Replace the `WorkflowAgentRunner _runner` field/ctor param with `IAgentRuntime _runtime` (`using TectikaAgents.Core.Interfaces;`).
- Replace the invoke block (the `BuildContextAsync` + `InvokeWithMessagesAsync` calls) with:
```csharp
        var threadId = await _runtime.EnsureThreadAsync(task, ct);
        // persist the (possibly new) thread id immediately so retries reuse it
        await _cosmos.PatchTaskThreadIdAsync(input.BoardId, input.TaskId, threadId, ct);

        var userContent = await _contextManager.BuildUserContentAsync(role, task, board, upstreamArtifacts, ct);

        // stream text deltas → agent_thinking (only meaningful for the real runtime)
        if (_runtime is TectikaAgents.AgentRuntime.FoundryAgentRuntime fr)
            fr.OnText = delta => _events.PublishAgentThinkingAsync(input.RunId, input.TaskId, input.Step, delta, ct).GetAwaiter().GetResult();

        var outcome = await _runtime.RunTurnAsync(
            new AgentRunRequest(role, task, threadId, userContent, _maxCompletionTokens, input.RunId, input.Step), ct);

        if (outcome.Status == AgentRunStatus.Failed)
            throw new Exception(outcome.Error ?? "Agent run failed.");
```
(`_maxCompletionTokens` is injected in Step 2; add `using TectikaAgents.Core.Models;` for `AgentRunRequest`/`AgentRunStatus` if not already present.)
- Map `outcome` into the existing artifact-save block: replace `result.Content`→`outcome.Content`, `result.ContentType`→`outcome.ContentType`, `result.CompletionId`→`outcome.CompletionId`, `result.InputTokens`→`outcome.TokenUsage.Input`, `result.OutputTokens`→`outcome.TokenUsage.Output`.
- Replace `ParseAgentSections(result.Content)` brief handling: if `outcome.BriefUpdate` is non-null use it directly; else keep `ParseAgentSections`.

- [ ] **Step 2: Inject MaxCompletionTokens**

Add `private readonly int _maxCompletionTokens;` and set it from `IOptions<FoundrySettings>` in the ctor:
```csharp
    public InvokeAgentActivity(WorkflowCosmosService cosmos, IAgentRuntime runtime, ContextManager contextManager,
        WorkflowEventPublisher events, Microsoft.Extensions.Options.IOptions<TectikaAgents.Core.Configuration.FoundrySettings> foundry,
        ILogger<InvokeAgentActivity> logger)
    {
        _cosmos = cosmos; _runtime = runtime; _contextManager = contextManager; _events = events;
        _maxCompletionTokens = foundry.Value.MaxCompletionTokens; _logger = logger;
    }
```
Remove the throwaway `maxTokens` line from Step 1.

- [ ] **Step 3: Add the thread-id patch + agent_thinking publisher if missing**

- In `src/workflows/Services/WorkflowCosmosService.cs`, add `PatchTaskThreadIdAsync` mirroring the existing `PatchTaskBriefAsync` (same partition/patch shape, patching the `foundryThreadId` field):
```csharp
    public async Task PatchTaskThreadIdAsync(string boardId, string taskId, string threadId, CancellationToken ct = default) =>
        await _tasks.PatchItemAsync<AgentTask>(taskId, new PartitionKey(boardId),
            new[] { PatchOperation.Set("/foundryThreadId", threadId) }, cancellationToken: ct);
```
  (Match the container field name `_tasks` and `using Microsoft.Azure.Cosmos;` as used by `PatchTaskBriefAsync` — open that method and copy its exact shape.)
- In `src/workflows/Services/WorkflowEventPublisher.cs`, add (confirmed mirror of `PublishStepStartedAsync`; `AgentEvent.Types.AgentThinking` already exists = `"agent_thinking"`):
```csharp
    public Task PublishAgentThinkingAsync(string runId, string taskId, int step, string text, CancellationToken ct = default) =>
        PublishAsync(new AgentEvent { Type = AgentEvent.Types.AgentThinking, RunId = runId, TaskId = taskId, Step = step, Content = text }, ct);
```

- [ ] **Step 4: Register the runtime in DI and delete the old runner**

In `src/workflows/Program.cs`, replace `builder.Services.AddHttpClient<WorkflowAgentRunner>();` with:
```csharp
var useMockAgents = builder.Configuration.GetValue<bool>("Foundry:UseMock",
    builder.Configuration.GetValue<bool>("MockDatabase:Enabled"));
if (useMockAgents)
    builder.Services.AddSingleton<IAgentRuntime, MockAgentRuntime>();
else
    builder.Services.AddSingleton<IAgentRuntime, FoundryAgentRuntime>();
```
Add `using TectikaAgents.Core.Interfaces;` and `using TectikaAgents.AgentRuntime;`. Add a ProjectReference to the AgentRuntime lib:
```bash
dotnet add src/workflows/TectikaAgents.Workflows.csproj reference src/agentruntime/TectikaAgents.AgentRuntime.csproj
```
Then delete the old runner:
```bash
git rm src/workflows/Services/WorkflowAgentRunner.cs
```

- [ ] **Step 5: Build workflows**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj`
Expected: Build succeeded (no references to `WorkflowAgentRunner` / `AgentInvocationResult` remain — remove any leftover usings).

- [ ] **Step 6: Commit**

```bash
git add -A src/workflows
git commit -m "feat(workflows): InvokeAgentActivity uses IAgentRuntime; drop WorkflowAgentRunner (T4)"
```

---

## Task 11: Eager agent provisioning in AgentRolesController; delete FoundryAgentService

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/AgentRolesController.cs`
- Modify: `src/api/TectikaAgents.Api/Program.cs`
- Delete: `src/api/TectikaAgents.Api/Services/FoundryAgentService.cs`

- [ ] **Step 1: Inject `IAgentProvisioner` and provision on Upsert/Delete**

In `AgentRolesController`:
- Add ctor param `IAgentProvisioner provisioner` (+ `using TectikaAgents.Core.Interfaces;`), store as `_provisioner`.
- In `Upsert`, after setting `role.TenantId`/`UpdatedAt` and **before** saving:
```csharp
        var sync = await _provisioner.EnsureAgentAsync(role, ct);
        // EnsureAgentAsync mutated role.FoundryAgentId / FoundryAgentHash on success
        var saved = await _cosmos.UpsertAgentRoleAsync(role, ct);
        return Ok(new { role = saved, synced = sync.Synced, error = sync.Error });
```
  (Replace the existing `var saved = ...; return Ok(saved);`.)
- Add a delete endpoint (the two Cosmos methods it needs don't exist yet — add them in Step 1b):
```csharp
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var role = await _cosmos.GetAgentRoleAsync(TenantId, id, ct);
        if (role is null) return NotFound();
        await _provisioner.DeleteAgentAsync(role.FoundryAgentId, ct);
        await _cosmos.DeleteAgentRoleAsync(TenantId, id, ct);
        return NoContent();
    }
```

- [ ] **Step 1b: Add `GetAgentRoleAsync` + `DeleteAgentRoleAsync` to the Cosmos service**

Confirmed: the API `ICosmosDbService` has only `GetAgentRolesAsync`/`UpsertAgentRoleAsync` — add a single-get and a delete, mirroring the existing `DeleteTaskAsync` pattern.

In `src/api/TectikaAgents.Api/Services/ICosmosDbService.cs` (next to the agent-role methods):
```csharp
    Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default);
    Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default);
```
In `src/api/TectikaAgents.Api/Services/InMemoryCosmosDbService.cs` (mirror `DeleteTaskAsync` at line ~88; agent roles are stored in `_agentRoles`):
```csharp
    public Task<AgentRole?> GetAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default)
        => Task.FromResult(_agentRoles.TryGetValue(roleId, out var r) && r.TenantId == tenantId ? r : null);

    public Task DeleteAgentRoleAsync(string tenantId, string roleId, CancellationToken ct = default)
    {
        _agentRoles.TryRemove(roleId, out _);
        return Task.CompletedTask;
    }
```
In `src/api/TectikaAgents.Api/Services/CosmosDbService.cs` (mirror its own `DeleteTaskAsync`/`GetTaskAsync` using the agent-roles container + `tenantId` partition key — open those methods and copy the exact `ReadItemAsync`/`DeleteItemAsync` shape, handling `CosmosException` 404 → null/no-op).

- [ ] **Step 2: Register `IAgentProvisioner` (mock/real) and drop FoundryAgentService**

In `src/api/TectikaAgents.Api/Program.cs`, replace `builder.Services.AddHttpClient<FoundryAgentService>();` with:
```csharp
var useMockAgents = builder.Configuration.GetValue<bool>("Foundry:UseMock", useMockDatabase);
if (useMockAgents)
    builder.Services.AddSingleton<IAgentProvisioner, MockAgentProvisioner>();
else
    builder.Services.AddSingleton<IAgentProvisioner, FoundryAgentRuntime>();
```
Add `using TectikaAgents.Core.Interfaces;` + `using TectikaAgents.AgentRuntime;`. Add the project reference:
```bash
dotnet add src/api/TectikaAgents.Api/TectikaAgents.Api.csproj reference src/agentruntime/TectikaAgents.AgentRuntime.csproj
```
Delete the unused service:
```bash
git rm src/api/TectikaAgents.Api/Services/FoundryAgentService.cs
```

- [ ] **Step 3: Build the API**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded (remove any leftover `FoundryAgentService` usings/refs).

- [ ] **Step 4: Commit**

```bash
git add -A src/api
git commit -m "feat(api): eager Foundry agent provisioning on AgentRoles upsert/delete; drop FoundryAgentService (T4)"
```

---

## Task 12: RunsController derives the pipeline from the task's assignee (TDD)

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/RunsController.cs`
- Create: `src/api/TectikaAgents.Api/Controllers/RunPipelineFactory.cs` (pure, testable)
- Test: `tests/TectikaAgents.Tests/RunPipelineFactoryTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/TectikaAgents.Tests/RunPipelineFactoryTests.cs`:
```csharp
using TectikaAgents.Api.Controllers;
using TectikaAgents.Core.Models;
using Xunit;

public class RunPipelineFactoryTests
{
    [Fact]
    public void BuildsSingleAgentStepFromAssignee()
    {
        var task = new AgentTask { Id = "t", Assignee = new TaskAssignee { Type = AssigneeType.Agent, Id = "role-dev" } };
        var pipeline = RunPipelineFactory.FromTask(task);
        Assert.Single(pipeline);
        Assert.Equal("role-dev", pipeline[0].AgentRoleId);
    }

    [Fact]
    public void ThrowsWhenAssigneeIsHuman()
    {
        var task = new AgentTask { Id = "t", Assignee = new TaskAssignee { Type = AssigneeType.Human, Id = "u1" } };
        Assert.Throws<InvalidOperationException>(() => RunPipelineFactory.FromTask(task));
    }
}
```
(Verify `PipelineStep` has an `AgentRoleId` property in `src/core/TectikaAgents.Core/Models/WorkflowRun.cs`; if the property name differs, match it.)

- [ ] **Step 2: Run the test, verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter RunPipelineFactoryTests`
Expected: FAIL (compile error — `RunPipelineFactory` does not exist). Add the API project reference to tests if needed: `dotnet add tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj reference src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`.

- [ ] **Step 3: Implement the factory**

`src/api/TectikaAgents.Api/Controllers/RunPipelineFactory.cs`:
```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Api.Controllers;

/// <summary>Builds a default single-step agent pipeline from a task's assigned agent.</summary>
public static class RunPipelineFactory
{
    public static List<PipelineStep> FromTask(AgentTask task)
    {
        if (task.Assignee.Type != AssigneeType.Agent || string.IsNullOrEmpty(task.Assignee.Id))
            throw new InvalidOperationException($"Task '{task.Id}' has no assigned agent.");
        return new List<PipelineStep>
        {
            new() { Step = 0, Type = StepType.AgentExecution, AgentRoleId = task.Assignee.Id }
        };
    }
}
```
(Verified against `WorkflowRun.cs`: `PipelineStep { int Step; StepType Type; string? AgentRoleId }`, `enum StepType { AgentExecution, ApprovalGate, CliBridge }`.)

- [ ] **Step 4: Use it in `RunsController.Start`**

In `RunsController.Start`, change the pipeline validation to fall back to the factory:
```csharp
        var pipeline = (req.Pipeline is { Count: > 0 }) ? req.Pipeline : RunPipelineFactory.FromTask(task);
```
and use `pipeline` thereafter (in `PipelineDefinition` and the durable input `steps`). Make `StartRunRequest.Pipeline` nullable: `List<PipelineStep>? Pipeline`.

- [ ] **Step 5: Run the test + build**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter RunPipelineFactoryTests`
Expected: PASS.
Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add -A src/api tests/TectikaAgents.Tests
git commit -m "feat(api): derive run pipeline from task assignee when none supplied"
```

---

## Task 13: Infra — project endpoint output + env wiring (idempotency rule)

**Files:**
- Modify: `infra/modules/foundry.bicep`
- Modify: `infra/modules/containerapps.bicep`
- Modify: `infra/modules/functionapp.bicep`
- Modify: `infra/main.bicep`
- Modify: `infra/deploy.ps1`

- [ ] **Step 1: Output the project endpoint from foundry.bicep**

In `infra/modules/foundry.bicep`, add after the existing `endpoint` output:
```bicep
// Agent Service (data-plane) project endpoint for Azure.AI.Agents.Persistent.
output projectEndpoint string = 'https://${customSubDomain}.services.ai.azure.com/api/projects/${project.name}'
```

- [ ] **Step 2: Pass it into the API + Function App env**

- In `infra/modules/containerapps.bicep`: add a param `param foundryProjectEndpoint string` and an env var on the API container: `{ name: 'Foundry__ProjectEndpoint', value: foundryProjectEndpoint }`.
- In `infra/modules/functionapp.bicep`: add `param foundryProjectEndpoint string` and the app setting `{ name: 'Foundry__ProjectEndpoint', value: foundryProjectEndpoint }`.
- In `infra/main.bicep`: pass `foundryProjectEndpoint: foundry.outputs.projectEndpoint` into both the `containerApps` and `functionApp` module params.

- [ ] **Step 3: (Optional) surface it from deploy.ps1**

If `deploy.ps1` echoes Foundry outputs, add `projectEndpoint` to the printed summary. No GitHub secret needed (it's non-sensitive env baked by Bicep).

- [ ] **Step 4: Validate the Bicep compiles**

Run: `cd /home/elimeshi/projects/repos/TectikaAgentEnvironment/infra && az bicep build --file main.bicep --stdout > /dev/null`
Expected: exit 0, no diagnostics.

- [ ] **Step 5: Commit**

```bash
git add infra
git commit -m "feat(infra): output Foundry project endpoint + wire Foundry__ProjectEndpoint env"
```

---

## Task 14: Frontend — Agents tab sync indicator + run uses assignee

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`
- Modify: `src/web/tectika-board/src/lib/api.ts`
- Modify: `src/web/tectika-board/src/app/agents/page.tsx`

- [ ] **Step 1: Type the upsert response**

In `src/lib/types.ts`, add (near the AgentRole type):
```ts
export interface AgentUpsertResult { role: AgentRole; synced: boolean; error?: string | null; }
```

- [ ] **Step 2: Surface synced/error after save**

In `src/lib/api.ts`, make the agent-role upsert return `AgentUpsertResult` (the API now returns `{ role, synced, error }`). In `src/app/agents/page.tsx`, after saving show a small badge: `synced ? "✓ synced" : "⚠ not synced: {error}"` next to the agent. Keep it minimal — a colored text/badge is enough.

- [ ] **Step 3: Ensure run-start doesn't require a client pipeline**

If the board run UI sends `pipeline`, it can keep doing so; otherwise it may now POST `/api/runs/start` with `{ taskId, boardId }` and the server derives the pipeline from the assignee (Task 12). Verify the existing call site in `src/lib/api.ts`/board UI still compiles with `pipeline` optional.

- [ ] **Step 4: Build the web app**

Run: `cd src/web/tectika-board && npm run build`
Expected: build succeeds (or `npm run lint` if build needs the API). Fix type errors only.

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src
git commit -m "feat(web): Agents tab sync indicator; run-start pipeline optional"
```

---

## Task 15: Full verification (mock E2E + Azure smoke)

**Files:** none (verification only). See the memory note "Running AgentBoard for visual QA" for launch specifics in this WSL box.

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj`
Expected: all PASS.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build TectikaAgents.slnx`
Expected: Build succeeded across core, agentruntime, api, workflows, tests.

- [ ] **Step 3: Mock E2E (no Azure)**

Launch API + Workflows with `MockDatabase:Enabled=true` and `Foundry:UseMock=true` (per the visual-QA memory). Then:
```bash
# create/update an agent → expect { synced: true, role.foundryAgentId: "mock-agent-..." }
curl -s -X POST localhost:<api>/api/agent-roles -H 'content-type: application/json' \
  -d '{"id":"role-dev","displayName":"Dev","systemPrompt":"code well","modelOverride":"gpt-4o"}'
# assign role-dev to a seeded task in the Boards UI (assignee.type=Agent, id=role-dev), then start a run:
curl -s -X POST localhost:<api>/api/runs/start -H 'content-type: application/json' \
  -d '{"taskId":"<taskId>","boardId":"<boardId>"}'
# stream events:
curl -N localhost:<api>/api/runs/<runId>/stream
```
Expected: SSE shows `step_started` → `agent_thinking` (mock emits at least one) → `step_completed`; an `Artifact` is created; the task's `foundryThreadId` is set (`mock-thread-...`); a second run accumulates `TaskBrief`.

- [ ] **Step 4: Azure smoke (real Foundry)**

With `Foundry:UseMock=false` and `Foundry__ProjectEndpoint` set to the live project endpoint, repeat Step 3 against Azure. Expected: a real agent is created (visible in the Foundry portal), a thread id like `thread_...` is persisted, and the artifact contains real model output with non-zero token usage.

- [ ] **Step 5: Commit any fixes found during verification**

```bash
git add -A && git commit -m "fix(phase1): address issues found during mock E2E / Azure smoke"
```

---

## Notes & risks
- **Beta SDK surface (Task 7):** member names (`Administration.CreateAgent`, `Runs.CreateRunStreaming`, `MessageContentUpdate`/`RunUpdate`) vary by version — lock them in the spike before Task 8 and adjust the code to match.
- **Durable replay:** all Foundry IO stays inside `InvokeAgentActivity` (an Activity), never the orchestrator. Streaming side-effects (event publishes) are fine there since the activity runs once.
- **Persisting ids:** `FoundryAgentRuntime` mutates `role.FoundryAgentId/Hash` and `task.FoundryThreadId` in-memory; the caller persists (controller saves the role; the activity patches the thread id).
- **Idempotency rule:** the infra change (Task 13) keeps `infra/` current — re-validate `az bicep build` before merging.
