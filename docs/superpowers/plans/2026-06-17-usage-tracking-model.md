# Usage & Cost Tracking Model — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace placeholder token/cost logic with a durable, model-aware usage system — an append-only usage ledger + materialized rollups (project/board/task), accurate token capture (incl. cached/reasoning), a provider-agnostic pricing catalog, full run-lifecycle handling, API endpoints, and UI.

**Architecture:** Each agent round/step writes one immutable `usageEvent` with full attribution and a frozen cost snapshot computed from a versioned pricing catalog. Cheap-to-read rollup docs (project/board/task) are incremented as events land, with race-safety via ETag-guarded read-modify-write retry. Reads (table, panels, dashboards) hit rollups, never scan events. Spec: [docs/superpowers/specs/2026-06-17-usage-tracking-model-design.md](../specs/2026-06-17-usage-tracking-model-design.md).

**Tech Stack:** C# (.NET, `TectikaAgents.Core`/`agentruntime`/`workflows`/`api`), xUnit (`tests/TectikaAgents.Tests`), Azure Cosmos DB, Azure Durable Functions, Next.js/React/TypeScript (`src/web/tectika-board`).

---

## Conventions & verification commands

- **.NET tests:** `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --nologo` (baseline: 99 passing).
- **.NET build:** `dotnet build src/<project>/<Project>.csproj --nologo`.
- **Web:** no unit-test infra for components; verify with `cd src/web/tectika-board && npm run build` and `npm run lint`. Pure TS logic (e.g. formatting helpers) may use `npm run test` (`node --test`).
- **Commit** after every task. Co-author trailer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Container names are duplicated between `CosmosDbService` (api) and `WorkflowCosmosService` (workflows) per existing convention — keep both in sync.

## Plan-level decision: rollup concurrency mechanism

The spec calls for race-safe rollup increments. This plan implements that via **ETag optimistic-concurrency read-modify-write with bounded retry** (read doc + ETag → mutate in memory → `ReplaceItemAsync` with `IfMatchEtag` → on `412 PreconditionFailed`, re-read and retry, capped at 8 attempts; create-if-`404`). This is fully race-safe and far easier to test than nested `PatchOperation.Increment` over dynamic `perModel` dictionary keys. Native patch-increment remains a valid future optimization.

## File structure

**Core (`src/core/TectikaAgents.Core`):**
- Modify `Models/WorkflowRun.cs` — extend `TokenUsage` (cached/reasoning).
- Create `Usage/ModelPrice.cs` — pricing catalog entry + `PricingCatalog`.
- Create `Usage/PricingCatalogLoader.cs` — loads embedded JSON catalog.
- Create `Resources/pricing-catalog.json` — the rates (embedded resource).
- Create `Usage/CostCalculator.cs` — rate resolution + cost computation.
- Create `Usage/UsageEvent.cs` — immutable ledger record.
- Create `Usage/UsageRollup.cs` — rollup doc + `UsageBucket`/`SessionBucket`.

**Agentruntime (`src/agentruntime`):**
- Modify `FoundryAgentRuntime.cs` — parse cached/reasoning; populate `TokenUsage`.
- Modify `AgentToolLoop.cs` — accumulate cached/reasoning.

**Workflows (`src/workflows`):**
- Create `Services/UsageRecorder.cs` — compute cost, write event (idempotent), increment rollups.
- Modify `Services/WorkflowCosmosService.cs` — usage-event write + rollup RMW + container consts.
- Modify `Activities/RunAgentRoundActivity.cs` — record usage per round.
- Modify `Activities/InvokeAgentActivity.cs` — record usage per step (incl. NeedsRevision).
- Modify `Program.cs` (workflows DI) — register `CostCalculator` + `UsageRecorder`.
- Modify the compaction activity/orchestrator — record summarization usage.

**API (`src/api/TectikaAgents.Api`):**
- Modify `Services/CosmosDbService.cs` + `ICosmosDbService.cs` + `InMemoryCosmosDbService.cs` — container defs + rollup/event/pricing reads.
- Create `Controllers/UsageController.cs` — endpoints.
- Modify `Services/ChatService.cs` — `/clear` session reset.
- Create `Services/UsageBackfill.cs` — synthesize rollups from existing runs.
- Modify `Services/MockData/MockDataSeeder.cs` — seed multi-model usage events + rollups.

**Infra (`infra/`):**
- Modify `infra/modules/data.bicep` — add the two containers.

**Web (`src/web/tectika-board/src`):**
- Modify `lib/types.ts` — extend `TokenUsage`; add usage rollup types.
- Modify `lib/api.ts` — usage endpoints.
- Modify `lib/columns.ts` — table reads `currentSession`.
- Create `components/workspace/UsagePanel.tsx` — session⇄lifetime + per-model.
- Modify `components/workspace/ItemPanel.tsx` — mount the panel.
- Modify dashboards/analytics pages — per-model + rollup reads.
- Create `app/settings/pricing/page.tsx` (or equivalent) — read-only catalog view.

---

# Phase 1 — Core models, pricing catalog, cost calculator

### Task 1: Extend `TokenUsage` with cached + reasoning

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/WorkflowRun.cs:111-121`
- Test: `tests/TectikaAgents.Tests/TokenUsageTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class TokenUsageTests
{
    [Fact]
    public void Total_is_input_plus_output_only()
    {
        var u = new TokenUsage { Input = 1000, CachedInput = 400, Output = 200, Reasoning = 50 };
        Assert.Equal(1200, u.Total);   // total = input + output; cached/reasoning are subsets, not added
    }

    [Fact]
    public void New_fields_default_to_zero()
    {
        var u = new TokenUsage { Input = 10, Output = 5 };
        Assert.Equal(0, u.CachedInput);
        Assert.Equal(0, u.Reasoning);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter TokenUsageTests --nologo`
Expected: FAIL — `CachedInput`/`Reasoning` do not exist.

- [ ] **Step 3: Implement**

Replace `TokenUsage` in `WorkflowRun.cs`:

```csharp
public class TokenUsage
{
    [JsonPropertyName("input")]
    public int Input { get; set; }

    /// <summary>Subset of <see cref="Input"/> served from cache — billed at the cached rate.</summary>
    [JsonPropertyName("cachedInput")]
    public int CachedInput { get; set; }

    [JsonPropertyName("output")]
    public int Output { get; set; }

    /// <summary>Subset of <see cref="Output"/> spent on reasoning — informational; already inside Output.</summary>
    [JsonPropertyName("reasoning")]
    public int Reasoning { get; set; }

    [JsonPropertyName("total")]
    public int Total => Input + Output;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter TokenUsageTests --nologo`
Expected: PASS. Then full suite — `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --nologo` — expected still 99+ passing (new fields are additive).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/WorkflowRun.cs tests/TectikaAgents.Tests/TokenUsageTests.cs
git commit -m "feat(core): extend TokenUsage with cached + reasoning tokens"
```

---

### Task 2: Pricing catalog model + JSON resource + loader

**Files:**
- Create: `src/core/TectikaAgents.Core/Usage/ModelPrice.cs`
- Create: `src/core/TectikaAgents.Core/Resources/pricing-catalog.json`
- Create: `src/core/TectikaAgents.Core/Usage/PricingCatalogLoader.cs`
- Modify: `src/core/TectikaAgents.Core/TectikaAgents.Core.csproj` (embed the JSON)
- Test: `tests/TectikaAgents.Tests/PricingCatalogTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using TectikaAgents.Core.Usage;
using Xunit;

namespace TectikaAgents.Tests;

public class PricingCatalogTests
{
    [Fact]
    public void Embedded_catalog_loads_and_has_a_version_and_gpt4o()
    {
        var catalog = PricingCatalogLoader.LoadEmbedded();
        Assert.False(string.IsNullOrWhiteSpace(catalog.Version));
        Assert.Contains(catalog.Prices, p => p.Provider == "azure-foundry" && p.Model == "gpt-4o");
    }

    [Fact]
    public void Gpt4o_output_rate_exceeds_input_rate()
    {
        var catalog = PricingCatalogLoader.LoadEmbedded();
        var p = catalog.Prices.Single(x => x.Provider == "azure-foundry" && x.Model == "gpt-4o");
        Assert.True(p.OutputPerMillion > p.InputPerMillion);
        Assert.True(p.CachedInputPerMillion < p.InputPerMillion);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter PricingCatalogTests --nologo`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the model**

`src/core/TectikaAgents.Core/Usage/ModelPrice.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Usage;

/// <summary>One effective-dated price row for a (provider, model) pair. Rates are per MILLION tokens.</summary>
public class ModelPrice
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("modelVersion")] public string? ModelVersion { get; set; }
    [JsonPropertyName("inputPerMillion")] public decimal InputPerMillion { get; set; }
    [JsonPropertyName("cachedInputPerMillion")] public decimal CachedInputPerMillion { get; set; }
    [JsonPropertyName("outputPerMillion")] public decimal OutputPerMillion { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
    [JsonPropertyName("effectiveFrom")] public DateTimeOffset EffectiveFrom { get; set; }
}

public class PricingCatalog
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("prices")] public List<ModelPrice> Prices { get; set; } = [];
}
```

- [ ] **Step 4: Add the JSON resource**

`src/core/TectikaAgents.Core/Resources/pricing-catalog.json`:

```json
{
  "version": "2026-06-17",
  "prices": [
    {
      "provider": "azure-foundry",
      "model": "gpt-4o",
      "inputPerMillion": 2.50,
      "cachedInputPerMillion": 1.25,
      "outputPerMillion": 10.00,
      "currency": "USD",
      "effectiveFrom": "2024-01-01T00:00:00Z"
    }
  ]
}
```

In `TectikaAgents.Core.csproj`, add inside an `<ItemGroup>`:

```xml
<EmbeddedResource Include="Resources/pricing-catalog.json" />
```

- [ ] **Step 5: Implement the loader**

`src/core/TectikaAgents.Core/Usage/PricingCatalogLoader.cs`:

```csharp
using System.Reflection;
using System.Text.Json;

namespace TectikaAgents.Core.Usage;

public static class PricingCatalogLoader
{
    private const string ResourceName = "TectikaAgents.Core.Resources.pricing-catalog.json";

    public static PricingCatalog LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded pricing catalog '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<PricingCatalog>(json)
            ?? throw new InvalidOperationException("Pricing catalog deserialized to null.");
    }
}
```

> Note: the embedded resource name is `<RootNamespace>.<folder-with-dots>.<file>`. If the project sets a non-default `RootNamespace`, adjust `ResourceName`. Verify via the test in Step 6.

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter PricingCatalogTests --nologo`
Expected: PASS. If `LoadEmbedded` throws "not found", run `dotnet build` then check the actual resource name with a one-off `Assembly.GetManifestResourceNames()` and fix `ResourceName`.

- [ ] **Step 7: Commit**

```bash
git add src/core/TectikaAgents.Core/Usage/ModelPrice.cs src/core/TectikaAgents.Core/Usage/PricingCatalogLoader.cs src/core/TectikaAgents.Core/Resources/pricing-catalog.json src/core/TectikaAgents.Core/TectikaAgents.Core.csproj tests/TectikaAgents.Tests/PricingCatalogTests.cs
git commit -m "feat(core): provider-agnostic pricing catalog (embedded JSON + loader)"
```

---

### Task 3: `CostCalculator`

**Files:**
- Create: `src/core/TectikaAgents.Core/Usage/CostCalculator.cs`
- Test: `tests/TectikaAgents.Tests/CostCalculatorTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;
using Xunit;

namespace TectikaAgents.Tests;

public class CostCalculatorTests
{
    private static CostCalculator Make() => new(new PricingCatalog
    {
        Version = "test-v1",
        Prices =
        {
            new ModelPrice { Provider = "azure-foundry", Model = "gpt-4o",
                InputPerMillion = 2.50m, CachedInputPerMillion = 1.25m, OutputPerMillion = 10.00m,
                Currency = "USD", EffectiveFrom = DateTimeOffset.Parse("2024-01-01T00:00:00Z") },
            new ModelPrice { Provider = "azure-foundry", Model = "gpt-4o",
                InputPerMillion = 3.00m, CachedInputPerMillion = 1.50m, OutputPerMillion = 12.00m,
                Currency = "USD", EffectiveFrom = DateTimeOffset.Parse("2026-01-01T00:00:00Z") },
        }
    });

    [Fact]
    public void Computes_cost_with_separate_input_cached_output_rates()
    {
        var c = Make();
        var usage = new TokenUsage { Input = 1_000_000, CachedInput = 200_000, Output = 500_000 };
        var r = c.Compute("azure-foundry", "gpt-4o", usage, DateTimeOffset.Parse("2026-06-01T00:00:00Z"));
        // newest rate effective <= 2026-06: input 3.00, cached 1.50, output 12.00
        // (1_000_000-200_000)/1e6*3.00 + 200_000/1e6*1.50 + 500_000/1e6*12.00 = 2.40 + 0.30 + 6.00
        Assert.False(r.PricingMissing);
        Assert.Equal(8.70m, r.CostUsd);
        Assert.Equal("USD", r.Currency);
    }

    [Fact]
    public void Picks_rate_effective_at_timestamp()
    {
        var c = Make();
        var usage = new TokenUsage { Input = 1_000_000, Output = 0 };
        var early = c.Compute("azure-foundry", "gpt-4o", usage, DateTimeOffset.Parse("2024-06-01T00:00:00Z"));
        Assert.Equal(2.50m, early.CostUsd);   // old rate
    }

    [Fact]
    public void Missing_model_flags_pricingMissing_and_zero_cost()
    {
        var c = Make();
        var r = c.Compute("anthropic", "claude-opus-4-8", new TokenUsage { Input = 100, Output = 100 }, DateTimeOffset.UtcNow);
        Assert.True(r.PricingMissing);
        Assert.Equal(0m, r.CostUsd);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CostCalculatorTests --nologo`
Expected: FAIL — `CostCalculator` does not exist.

- [ ] **Step 3: Implement**

`src/core/TectikaAgents.Core/Usage/CostCalculator.cs`:

```csharp
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Usage;

public sealed class CostResult
{
    public decimal CostUsd { get; init; }
    public bool PricingMissing { get; init; }
    public string CatalogVersion { get; init; } = "";
    public decimal InputPerMillion { get; init; }
    public decimal CachedInputPerMillion { get; init; }
    public decimal OutputPerMillion { get; init; }
    public string Currency { get; init; } = "USD";
}

/// <summary>Pure cost computation over a pricing catalog. No I/O. Cost is frozen by callers onto events.</summary>
public sealed class CostCalculator
{
    private readonly PricingCatalog _catalog;
    public CostCalculator(PricingCatalog catalog) => _catalog = catalog;

    public string CatalogVersion => _catalog.Version;
    public IReadOnlyList<ModelPrice> Prices => _catalog.Prices;

    public ModelPrice? Resolve(string provider, string model, DateTimeOffset at) =>
        _catalog.Prices
            .Where(p => p.Provider == provider && p.Model == model && p.EffectiveFrom <= at)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefault();

    public CostResult Compute(string provider, string model, TokenUsage usage, DateTimeOffset at)
    {
        var price = Resolve(provider, model, at);
        if (price is null)
            return new CostResult { PricingMissing = true, CatalogVersion = _catalog.Version };

        var nonCachedInput = Math.Max(0, usage.Input - usage.CachedInput);
        var cost =
            nonCachedInput / 1_000_000m * price.InputPerMillion +
            usage.CachedInput / 1_000_000m * price.CachedInputPerMillion +
            usage.Output / 1_000_000m * price.OutputPerMillion;

        return new CostResult
        {
            CostUsd = decimal.Round(cost, 6),
            PricingMissing = false,
            CatalogVersion = _catalog.Version,
            InputPerMillion = price.InputPerMillion,
            CachedInputPerMillion = price.CachedInputPerMillion,
            OutputPerMillion = price.OutputPerMillion,
            Currency = price.Currency,
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter CostCalculatorTests --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Usage/CostCalculator.cs tests/TectikaAgents.Tests/CostCalculatorTests.cs
git commit -m "feat(core): model-aware CostCalculator with effective-dated rates"
```

---

### Task 4: `UsageEvent` model

**Files:**
- Create: `src/core/TectikaAgents.Core/Usage/UsageEvent.cs`
- Test: `tests/TectikaAgents.Tests/UsageEventTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using TectikaAgents.Core.Usage;
using Xunit;

namespace TectikaAgents.Tests;

public class UsageEventTests
{
    [Fact]
    public void DeterministicId_is_run_step_invocation_round()
    {
        var id = UsageEvent.MakeId("run-1", 2, "inv-abc", 3);
        Assert.Equal("run-1:2:inv-abc:3", id);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter UsageEventTests --nologo`
Expected: FAIL — `UsageEvent` does not exist.

- [ ] **Step 3: Implement**

`src/core/TectikaAgents.Core/Usage/UsageEvent.cs`:

```csharp
using System.Text.Json.Serialization;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Usage;

/// <summary>Immutable ledger record for one billed LLM unit (a round, or a whole pipeline step).
/// Partition key: /taskId. Id is deterministic so write-level redeliveries dedupe via 409.</summary>
public class UsageEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = "";
    [JsonPropertyName("boardId")] public string BoardId { get; set; } = "";
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("step")] public int Step { get; set; }
    [JsonPropertyName("round")] public int Round { get; set; }
    [JsonPropertyName("agentRoleId")] public string AgentRoleId { get; set; } = "";
    [JsonPropertyName("agentRoleName")] public string AgentRoleName { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("modelVersion")] public string? ModelVersion { get; set; }
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("usage")] public TokenUsage Usage { get; set; } = new();
    [JsonPropertyName("catalogVersion")] public string CatalogVersion { get; set; } = "";
    [JsonPropertyName("inputPerMillion")] public decimal InputPerMillion { get; set; }
    [JsonPropertyName("cachedInputPerMillion")] public decimal CachedInputPerMillion { get; set; }
    [JsonPropertyName("outputPerMillion")] public decimal OutputPerMillion { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
    [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }
    [JsonPropertyName("pricingMissing")] public bool PricingMissing { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public static string MakeId(string runId, int step, string invocationId, int round) =>
        $"{runId}:{step}:{invocationId}:{round}";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter UsageEventTests --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Usage/UsageEvent.cs tests/TectikaAgents.Tests/UsageEventTests.cs
git commit -m "feat(core): UsageEvent ledger model with deterministic id"
```

---

### Task 5: `UsageRollup` model + buckets

**Files:**
- Create: `src/core/TectikaAgents.Core/Usage/UsageRollup.cs`
- Test: `tests/TectikaAgents.Tests/UsageRollupTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;
using Xunit;

namespace TectikaAgents.Tests;

public class UsageRollupTests
{
    [Fact]
    public void Add_accumulates_tokens_cost_and_count()
    {
        var b = new UsageBucket();
        b.Add(new TokenUsage { Input = 100, CachedInput = 10, Output = 50, Reasoning = 5 }, 0.25m);
        b.Add(new TokenUsage { Input = 200, Output = 100 }, 0.50m);
        Assert.Equal(300, b.Tokens.Input);
        Assert.Equal(10, b.Tokens.CachedInput);
        Assert.Equal(150, b.Tokens.Output);
        Assert.Equal(0.75m, b.CostUsd);
        Assert.Equal(2, b.EventCount);
    }

    [Fact]
    public void Ids_compose_by_scope()
    {
        Assert.Equal("project:tenant-1", UsageRollup.ProjectId("tenant-1"));
        Assert.Equal("board:board-1", UsageRollup.BoardId("board-1"));
        Assert.Equal("task:task-1", UsageRollup.TaskId("task-1"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter UsageRollupTests --nologo`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

`src/core/TectikaAgents.Core/Usage/UsageRollup.cs`:

```csharp
using System.Text.Json.Serialization;
using TectikaAgents.Core.Models;

namespace TectikaAgents.Core.Usage;

/// <summary>Mutable accumulator of tokens + cost over some scope. Note: TokenUsage.Total is computed,
/// so we store the component fields and let Total derive.</summary>
public class UsageBucket
{
    [JsonPropertyName("tokens")] public TokenUsage Tokens { get; set; } = new();
    [JsonPropertyName("costUsd")] public decimal CostUsd { get; set; }
    [JsonPropertyName("eventCount")] public int EventCount { get; set; }

    public void Add(TokenUsage u, decimal costUsd)
    {
        Tokens.Input += u.Input;
        Tokens.CachedInput += u.CachedInput;
        Tokens.Output += u.Output;
        Tokens.Reasoning += u.Reasoning;
        CostUsd += costUsd;
        EventCount += 1;
    }
}

public class SessionBucket : UsageBucket
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("since")] public DateTimeOffset Since { get; set; } = DateTimeOffset.UtcNow;
}

public enum UsageScope { Project, Board, Task }

/// <summary>Materialized rollup at project/board/task scope. Partition key: /tenantId.</summary>
public class UsageRollup
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = "";
    [JsonPropertyName("scope")] public UsageScope Scope { get; set; }
    [JsonPropertyName("scopeId")] public string ScopeId { get; set; } = "";
    [JsonPropertyName("lifetime")] public UsageBucket Lifetime { get; set; } = new();

    /// <summary>Per-model breakdown keyed by "provider/model".</summary>
    [JsonPropertyName("perModel")] public Dictionary<string, UsageBucket> PerModel { get; set; } = new();

    /// <summary>Task scope only — usage since the last /clear. Null for project/board scope.</summary>
    [JsonPropertyName("currentSession")] public SessionBucket? CurrentSession { get; set; }

    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string ProjectId(string tenantId) => $"project:{tenantId}";
    public static string BoardId(string boardId) => $"board:{boardId}";
    public static string TaskId(string taskId) => $"task:{taskId}";
    public static string ModelKey(string provider, string model) => $"{provider}/{model}";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter UsageRollupTests --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Usage/UsageRollup.cs tests/TectikaAgents.Tests/UsageRollupTests.cs
git commit -m "feat(core): UsageRollup model with lifetime/perModel/currentSession buckets"
```

---

# Phase 2 — Runtime token capture (cached + reasoning)

### Task 6: Parse cached/reasoning tokens in the runtime + accumulate in the loop

**Files:**
- Modify: `src/agentruntime/FoundryAgentRuntime.cs:373-377` (UsageInfo DTO), `:188`, `:245` (TokenUsage population)
- Modify: `src/agentruntime/AgentToolLoop.cs:71-73` (accumulate)
- Test: `tests/TectikaAgents.Tests/AgentToolLoopUsageTests.cs` (create)

- [ ] **Step 1: Write the failing test (loop accumulation of all four fields)**

The loop is HTTP-free and driven by a `SendRound` delegate, so it is unit-testable.

```csharp
using TectikaAgents.AgentRuntime;
using TectikaAgents.Core.Models;
using Xunit;

namespace TectikaAgents.Tests;

public class AgentToolLoopUsageTests
{
    [Fact]
    public async Task Accumulates_input_cached_output_reasoning_across_rounds()
    {
        var explorer = new NoopExplorer();
        var loop = new AgentToolLoop(explorer);
        var round = 0;
        AgentToolLoop.SendRound send = (outputs, ct) =>
        {
            round++;
            // Round 1 returns final text with usage (no tool calls) → loop ends.
            var usage = new TokenUsage { Input = 100, CachedInput = 40, Output = 30, Reasoning = 10 };
            return Task.FromResult(RoundResponse.Final("done", usage));
        };

        var result = await loop.RunAsync(send, maxRounds: 5, onToolCall: (_, _) => { }, CancellationToken.None);

        Assert.Equal(100, result.Usage.Input);
        Assert.Equal(40, result.Usage.CachedInput);
        Assert.Equal(30, result.Usage.Output);
        Assert.Equal(10, result.Usage.Reasoning);
    }

    private sealed class NoopExplorer : IProjectExplorer
    {
        // Implement IProjectExplorer members as no-ops / throw NotSupportedException.
        // Copy the minimal stub from existing runtime tests if one exists; otherwise implement
        // each interface method to throw NotSupportedException (the test never calls tools).
    }
}
```

> Before writing the stub, check `tests/TectikaAgents.Tests` for an existing `IProjectExplorer` fake and reuse it. If none exists, implement each member to `throw new NotSupportedException()` — the Final-on-round-1 path never invokes explorer methods.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter AgentToolLoopUsageTests --nologo`
Expected: FAIL — `result.Usage.CachedInput`/`Reasoning` are 0 because the loop doesn't accumulate them yet.

- [ ] **Step 3: Implement accumulation in `AgentToolLoop.cs`**

Replace lines 71-73:

```csharp
result.Usage = new TokenUsage {
    Input = result.Usage.Input + resp.Usage.Input,
    CachedInput = result.Usage.CachedInput + resp.Usage.CachedInput,
    Output = result.Usage.Output + resp.Usage.Output,
    Reasoning = result.Usage.Reasoning + resp.Usage.Reasoning };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter AgentToolLoopUsageTests --nologo`
Expected: PASS.

- [ ] **Step 5: Implement runtime DTO + population (build-verified, not unit-tested — HTTP layer)**

In `FoundryAgentRuntime.cs`, extend `UsageInfo` (lines 373-377):

```csharp
private sealed class UsageInfo
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("input_tokens_details")] public InputTokenDetails? InputTokenDetails { get; set; }
    [JsonPropertyName("output_tokens_details")] public OutputTokenDetails? OutputTokenDetails { get; set; }
}
private sealed class InputTokenDetails
{
    [JsonPropertyName("cached_tokens")] public int CachedTokens { get; set; }
}
private sealed class OutputTokenDetails
{
    [JsonPropertyName("reasoning_tokens")] public int ReasoningTokens { get; set; }
}
```

> Note: the Responses API nests cached tokens under `input_tokens_details.cached_tokens` and reasoning under `output_tokens_details.reasoning_tokens`. If a deployment instead returns `prompt_tokens_details`/`completion_tokens_details`, add those property names as alternates. Missing → 0.

Then replace BOTH `TokenUsage` constructions (line 188 in `RunTurnAsync`, line 245 in `RunRoundAsync`):

```csharp
var usage = new TokenUsage {
    Input = r.Usage?.InputTokens ?? 0,
    CachedInput = r.Usage?.InputTokenDetails?.CachedTokens ?? 0,
    Output = r.Usage?.OutputTokens ?? 0,
    Reasoning = r.Usage?.OutputTokenDetails?.ReasoningTokens ?? 0 };
```

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/agentruntime/TectikaAgents.AgentRuntime.csproj --nologo`
Expected: build succeeds. Then full suite: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --nologo` — all green.

- [ ] **Step 7: Commit**

```bash
git add src/agentruntime/FoundryAgentRuntime.cs src/agentruntime/AgentToolLoop.cs tests/TectikaAgents.Tests/AgentToolLoopUsageTests.cs
git commit -m "feat(agentruntime): capture cached + reasoning tokens from Foundry responses"
```

---

# Phase 3 — Persistence, rollups, and write-path hooks

### Task 7: Add `usageEvents` + `usageRollups` containers

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/CosmosDbService.cs:34-49` (+ name consts near other container consts)
- Modify: `src/workflows/Services/WorkflowCosmosService.cs` (add matching name consts)
- Modify: `infra/modules/data.bicep`
- Test: `tests/TectikaAgents.Tests/ContainerDefinitionsTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using TectikaAgents.Api.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class ContainerDefinitionsTests
{
    [Fact]
    public void Includes_usage_containers_with_correct_partition_keys()
    {
        var defs = CosmosDbService.ContainerDefinitions;
        Assert.Contains(defs, d => d.Name == "usageEvents" && d.PartitionKey == "/taskId");
        Assert.Contains(defs, d => d.Name == "usageRollups" && d.PartitionKey == "/tenantId");
    }
}
```

> If `tests` doesn't already reference the api project, this test won't compile. Check existing tests for an api reference; if absent, instead assert against a constant you place in Core, or skip this test and verify by the integration in Task 8. (Most likely the api is referenced — `EdgeBackfill`/seeders are tested.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter ContainerDefinitionsTests --nologo`
Expected: FAIL — containers not in the array.

- [ ] **Step 3: Implement**

In `CosmosDbService.cs`, add name constants alongside the existing container-name consts (e.g. near `RunEventsContainer`):

```csharp
public const string UsageEventsContainer = "usageEvents";
public const string UsageRollupsContainer = "usageRollups";
```

Add to `ContainerDefinitions` (after `UserSettingsContainer`):

```csharp
        (UsageEventsContainer,       "/taskId"),
        (UsageRollupsContainer,      "/tenantId"),
```

In `WorkflowCosmosService.cs`, add matching consts (the workflow service uses its own names):

```csharp
public const string UsageEventsContainer = "usageEvents";
public const string UsageRollupsContainer = "usageRollups";
```

- [ ] **Step 4: Update infra (idempotency rule — infra MUST stay current)**

In `infra/modules/data.bicep`, mirror however containers are declared there (find the existing `runEvents` container resource and copy its shape). Add two containers: `usageEvents` partition key `/taskId`, `usageRollups` partition key `/tenantId`. Match the existing throughput/indexing settings used by sibling containers.

- [ ] **Step 5: Run test + build**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter ContainerDefinitionsTests --nologo`
Expected: PASS. Build api + workflows.

> Deployment note (from project memory): new Cosmos containers are NOT auto-created reliably (`EnsureInfrastructureAsync` swallows failures). After deploy, create them explicitly if missing: `az cosmosdb sql container create -g <rg> -a <acct> -d <db> -n usageEvents --partition-key-path /taskId` and `... -n usageRollups --partition-key-path /tenantId`.

- [ ] **Step 6: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/CosmosDbService.cs src/workflows/Services/WorkflowCosmosService.cs infra/modules/data.bicep tests/TectikaAgents.Tests/ContainerDefinitionsTests.cs
git commit -m "feat(infra): add usageEvents + usageRollups Cosmos containers"
```

---

### Task 8: Workflow Cosmos methods — idempotent event write + ETag rollup RMW

**Files:**
- Modify: `src/workflows/Services/WorkflowCosmosService.cs`
- Test: covered by `UsageRecorder` tests in Task 9 (this task adds the Cosmos plumbing; the testable logic — bucket math, retry decision — is exercised there).

- [ ] **Step 1: Implement the idempotent event write**

Add to `WorkflowCosmosService` (uses the `C(name)` accessor, `Microsoft.Azure.Cosmos`):

```csharp
using Microsoft.Azure.Cosmos;
using TectikaAgents.Core.Usage;

// ... inside the class ...

/// <summary>Writes a usage event. Returns true if newly created, false if it already existed
/// (409 Conflict) — callers skip rollup increments on false to avoid double counting.</summary>
public async Task<bool> TryCreateUsageEventAsync(UsageEvent ev, CancellationToken ct = default)
{
    try
    {
        await C(UsageEventsContainer).CreateItemAsync(ev, new PartitionKey(ev.TaskId), cancellationToken: ct);
        return true;
    }
    catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.Conflict)
    {
        return false;   // already recorded (write-level redelivery) — do not re-increment rollups
    }
}
```

- [ ] **Step 2: Implement the ETag-guarded rollup read-modify-write**

```csharp
/// <summary>Applies <paramref name="mutate"/> to the rollup with id <paramref name="id"/> under
/// tenant partition, race-safe via ETag optimistic concurrency with bounded retry. Creates the
/// rollup (via <paramref name="create"/>) if it does not exist.</summary>
public async Task UpdateRollupAsync(
    string tenantId, string id,
    Func<UsageRollup> create, Action<UsageRollup> mutate,
    CancellationToken ct = default)
{
    var container = C(UsageRollupsContainer);
    var pk = new PartitionKey(tenantId);

    for (var attempt = 0; attempt < 8; attempt++)
    {
        UsageRollup rollup;
        string? etag = null;
        try
        {
            var read = await container.ReadItemAsync<UsageRollup>(id, pk, cancellationToken: ct);
            rollup = read.Resource;
            etag = read.ETag;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            rollup = create();
        }

        mutate(rollup);
        rollup.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            if (etag is null)
                await container.CreateItemAsync(rollup, pk, cancellationToken: ct);
            else
                await container.ReplaceItemAsync(rollup, id, pk,
                    new ItemRequestOptions { IfMatchEtag = etag }, ct);
            return;
        }
        catch (CosmosException e) when (
            e.StatusCode == System.Net.HttpStatusCode.PreconditionFailed ||   // ETag mismatch — concurrent writer
            e.StatusCode == System.Net.HttpStatusCode.Conflict)               // create race — someone created it first
        {
            // re-read and retry
        }
    }
    throw new InvalidOperationException($"Rollup {id} update exhausted retries under contention.");
}
```

- [ ] **Step 3: Implement a task-rollup session reset helper (used by /clear in Task 13)**

```csharp
public async Task<UsageRollup?> GetRollupAsync(string tenantId, string id, CancellationToken ct = default)
{
    try
    {
        var read = await C(UsageRollupsContainer).ReadItemAsync<UsageRollup>(id, new PartitionKey(tenantId), cancellationToken: ct);
        return read.Resource;
    }
    catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj --nologo`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/workflows/Services/WorkflowCosmosService.cs
git commit -m "feat(workflows): idempotent usage-event write + ETag-safe rollup RMW"
```

---

### Task 9: `UsageRecorder` service

**Files:**
- Create: `src/workflows/Services/UsageRecorder.cs`
- Test: `tests/TectikaAgents.Tests/UsageRecorderTests.cs` (create) — tests the pure rollup-mutation logic by extracting it into a static helper.

- [ ] **Step 1: Write the failing test for the rollup-mutation helper**

The Cosmos round-trip isn't unit-tested, but the *mutation* (which buckets get which numbers) is pure and critical. Extract it into `UsageRecorder.ApplyToProjectOrBoard` / `ApplyToTask` static methods and test those.

```csharp
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;
using TectikaAgents.Workflows.Services;
using Xunit;

namespace TectikaAgents.Tests;

public class UsageRecorderTests
{
    [Fact]
    public void Apply_increments_lifetime_and_perModel()
    {
        var rollup = new UsageRollup { Id = UsageRollup.BoardId("b1"), TenantId = "t1", Scope = UsageScope.Board, ScopeId = "b1" };
        var usage = new TokenUsage { Input = 100, Output = 50 };

        UsageRecorder.ApplyShared(rollup, "azure-foundry", "gpt-4o", usage, 0.5m);

        Assert.Equal(100, rollup.Lifetime.Tokens.Input);
        Assert.Equal(0.5m, rollup.Lifetime.CostUsd);
        Assert.Equal(1, rollup.Lifetime.EventCount);
        Assert.True(rollup.PerModel.ContainsKey("azure-foundry/gpt-4o"));
        Assert.Equal(50, rollup.PerModel["azure-foundry/gpt-4o"].Tokens.Output);
    }

    [Fact]
    public void Apply_to_task_also_updates_currentSession_when_matching()
    {
        var rollup = new UsageRollup
        {
            Id = UsageRollup.TaskId("k1"), TenantId = "t1", Scope = UsageScope.Task, ScopeId = "k1",
            CurrentSession = new SessionBucket { SessionId = "sess-1" }
        };
        var usage = new TokenUsage { Input = 10, Output = 5 };

        UsageRecorder.ApplyTask(rollup, "azure-foundry", "gpt-4o", usage, 0.1m, "sess-1");

        Assert.Equal(10, rollup.Lifetime.Tokens.Input);
        Assert.Equal(5, rollup.CurrentSession!.Tokens.Output);
        Assert.Equal(1, rollup.CurrentSession.EventCount);
    }

    [Fact]
    public void Apply_to_task_starts_new_session_bucket_when_session_changed()
    {
        var rollup = new UsageRollup
        {
            Id = UsageRollup.TaskId("k1"), TenantId = "t1", Scope = UsageScope.Task, ScopeId = "k1",
            CurrentSession = new SessionBucket { SessionId = "old" }
        };
        UsageRecorder.ApplyTask(rollup, "azure-foundry", "gpt-4o", new TokenUsage { Input = 7 }, 0.0m, "new");

        Assert.Equal("new", rollup.CurrentSession!.SessionId);
        Assert.Equal(7, rollup.CurrentSession.Tokens.Input);   // fresh bucket, not old + 7
        Assert.Equal(7, rollup.Lifetime.Tokens.Input);          // lifetime always accumulates
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter UsageRecorderTests --nologo`
Expected: FAIL — `UsageRecorder` does not exist.

- [ ] **Step 3: Implement**

`src/workflows/Services/UsageRecorder.cs`:

```csharp
using Microsoft.Extensions.Logging;
using TectikaAgents.Core.Models;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Workflows.Services;

/// <summary>Computes cost and records usage: writes one idempotent UsageEvent, then increments the
/// project, board, and task rollups. Skips rollup increments when the event already existed.</summary>
public sealed class UsageRecorder
{
    private readonly WorkflowCosmosService _cosmos;
    private readonly CostCalculator _cost;
    private readonly ILogger<UsageRecorder> _logger;

    public UsageRecorder(WorkflowCosmosService cosmos, CostCalculator cost, ILogger<UsageRecorder> logger)
    {
        _cosmos = cosmos;
        _cost = cost;
        _logger = logger;
    }

    public sealed record Attribution(
        string TenantId, string BoardId, string TaskId, string RunId,
        int Step, int Round, string InvocationId,
        string AgentRoleId, string AgentRoleName,
        string Provider, string Model, string? ModelVersion, string SessionId);

    public async Task RecordAsync(Attribution a, TokenUsage usage, CancellationToken ct)
    {
        if (usage.Total == 0) return;   // nothing billable (e.g. provider omitted usage)

        var at = DateTimeOffset.UtcNow;
        var cost = _cost.Compute(a.Provider, a.Model, usage, at);
        if (cost.PricingMissing)
            _logger.LogWarning("[Usage] no pricing for {Provider}/{Model} — tokens tracked, cost=0", a.Provider, a.Model);

        var ev = new UsageEvent
        {
            Id = UsageEvent.MakeId(a.RunId, a.Step, a.InvocationId, a.Round),
            TenantId = a.TenantId, BoardId = a.BoardId, TaskId = a.TaskId, RunId = a.RunId,
            Step = a.Step, Round = a.Round,
            AgentRoleId = a.AgentRoleId, AgentRoleName = a.AgentRoleName,
            Provider = a.Provider, Model = a.Model, ModelVersion = a.ModelVersion,
            SessionId = a.SessionId, Usage = usage,
            CatalogVersion = cost.CatalogVersion,
            InputPerMillion = cost.InputPerMillion, CachedInputPerMillion = cost.CachedInputPerMillion,
            OutputPerMillion = cost.OutputPerMillion, Currency = cost.Currency,
            CostUsd = cost.CostUsd, PricingMissing = cost.PricingMissing, Timestamp = at,
        };

        var created = await _cosmos.TryCreateUsageEventAsync(ev, ct);
        if (!created)
        {
            _logger.LogDebug("[Usage] event {Id} already exists — skipping rollup increment", ev.Id);
            return;
        }

        await _cosmos.UpdateRollupAsync(a.TenantId, UsageRollup.ProjectId(a.TenantId),
            () => new UsageRollup { Id = UsageRollup.ProjectId(a.TenantId), TenantId = a.TenantId, Scope = UsageScope.Project, ScopeId = a.TenantId },
            r => ApplyShared(r, a.Provider, a.Model, usage, cost.CostUsd), ct);

        await _cosmos.UpdateRollupAsync(a.TenantId, UsageRollup.BoardId(a.BoardId),
            () => new UsageRollup { Id = UsageRollup.BoardId(a.BoardId), TenantId = a.TenantId, Scope = UsageScope.Board, ScopeId = a.BoardId },
            r => ApplyShared(r, a.Provider, a.Model, usage, cost.CostUsd), ct);

        await _cosmos.UpdateRollupAsync(a.TenantId, UsageRollup.TaskId(a.TaskId),
            () => new UsageRollup
            {
                Id = UsageRollup.TaskId(a.TaskId), TenantId = a.TenantId, Scope = UsageScope.Task, ScopeId = a.TaskId,
                CurrentSession = new SessionBucket { SessionId = a.SessionId, Since = at },
            },
            r => ApplyTask(r, a.Provider, a.Model, usage, cost.CostUsd, a.SessionId), ct);
    }

    /// <summary>Increment lifetime + perModel. Used for project/board scopes.</summary>
    public static void ApplyShared(UsageRollup r, string provider, string model, TokenUsage usage, decimal costUsd)
    {
        r.Lifetime.Add(usage, costUsd);
        var key = UsageRollup.ModelKey(provider, model);
        if (!r.PerModel.TryGetValue(key, out var bucket)) { bucket = new UsageBucket(); r.PerModel[key] = bucket; }
        bucket.Add(usage, costUsd);
    }

    /// <summary>Increment lifetime + perModel + currentSession (resetting the session bucket if the
    /// sessionId changed — i.e. a /clear happened since this rollup was last written).</summary>
    public static void ApplyTask(UsageRollup r, string provider, string model, TokenUsage usage, decimal costUsd, string sessionId)
    {
        ApplyShared(r, provider, model, usage, costUsd);
        if (r.CurrentSession is null || r.CurrentSession.SessionId != sessionId)
            r.CurrentSession = new SessionBucket { SessionId = sessionId, Since = DateTimeOffset.UtcNow };
        r.CurrentSession.Add(usage, costUsd);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --filter UsageRecorderTests --nologo`
Expected: PASS.

- [ ] **Step 5: Register in DI**

In the workflows `Program.cs`, register the calculator (singleton — catalog is immutable) and recorder (scoped/transient):

```csharp
builder.Services.AddSingleton(_ => new TectikaAgents.Core.Usage.CostCalculator(
    TectikaAgents.Core.Usage.PricingCatalogLoader.LoadEmbedded()));
builder.Services.AddScoped<TectikaAgents.Workflows.Services.UsageRecorder>();
```

> Match the existing DI registration style in that file (it may use `services.AddX` on a host builder). Find where `WorkflowCosmosService` is registered and place these nearby.

- [ ] **Step 6: Build to verify**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj --nologo`
Expected: succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/workflows/Services/UsageRecorder.cs src/workflows/Program.cs tests/TectikaAgents.Tests/UsageRecorderTests.cs
git commit -m "feat(workflows): UsageRecorder — cost + idempotent event + rollup increments"
```

---

### Task 10: Hook `RunAgentRoundActivity` (per-round, steerable)

**Files:**
- Modify: `src/workflows/Activities/RunAgentRoundActivity.cs` (ctor + after line 150)
- Verification: build + full suite (integration via Cosmos not unit-tested).

- [ ] **Step 1: Inject `UsageRecorder` and resolve model attribution**

Add `UsageRecorder` + `FoundrySettings` defaults to the ctor (the class already injects `IOptions<FoundrySettings>` for `_maxCompletionTokens`; capture the default model + provider too):

```csharp
private readonly UsageRecorder _usage;
private readonly string _defaultModel;
private readonly string _provider;
```

In the ctor body:

```csharp
_usage = usage;                                  // new ctor param: UsageRecorder usage
_defaultModel = foundry.Value.DefaultModel;
_provider = foundry.Value.IsOpenAiDirect ? "openai" : "azure-foundry";
```

Add `UsageRecorder usage` to the constructor parameter list.

- [ ] **Step 2: Record usage after persisting round events (after line 150)**

Insert right after the `foreach (... BuildRoundEvents ...)` block:

```csharp
// Record usage to the ledger + rollups (per round). Session = the task's current session id.
var model = role.ModelOverride ?? _defaultModel;
await _usage.RecordAsync(new UsageRecorder.Attribution(
    TenantId: input.TenantId, BoardId: input.BoardId, TaskId: input.TaskId, RunId: input.RunId,
    Step: 0, Round: input.Round, InvocationId: ctx.InvocationId,
    AgentRoleId: role.Id, AgentRoleName: role.DisplayName,
    Provider: _provider, Model: model, ModelVersion: null,
    SessionId: task.UsageSessionId ?? input.RunId),
    outcome.Usage, ct);
```

> `task.UsageSessionId` is added in Task 13. Until then this references a field that doesn't exist — implement Task 13's model field FIRST if doing tasks out of order. The fallback `?? input.RunId` keeps a sane session id for legacy tasks with no session stamp.

- [ ] **Step 3: Build + suite**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj --nologo` then full test suite.
Expected: build succeeds; tests green.

- [ ] **Step 4: Commit**

```bash
git add src/workflows/Activities/RunAgentRoundActivity.cs
git commit -m "feat(workflows): record usage per steerable round"
```

---

### Task 11: Hook `InvokeAgentActivity` (per-step, pipeline)

**Files:**
- Modify: `src/workflows/Activities/InvokeAgentActivity.cs` (ctor + the two `usage`/`usage0` sites at lines 188 and 225)
- Verification: build + suite.

- [ ] **Step 1: Inject `UsageRecorder` + defaults (mirror Task 10)**

Add `UsageRecorder usage` to the ctor, store `_usage`, `_defaultModel`, `_provider` (the class already injects `IOptions<FoundrySettings> foundry`).

- [ ] **Step 2: Record at the NeedsRevision return (after line 189, before the `return new StepResult`)**

```csharp
var model0 = role.ModelOverride ?? _defaultModel;
await _usage.RecordAsync(new UsageRecorder.Attribution(
    TenantId: input.TenantId, BoardId: input.BoardId, TaskId: input.TaskId, RunId: input.RunId,
    Step: input.Step, Round: 0, InvocationId: executionContext.InvocationId,
    AgentRoleId: role.Id, AgentRoleName: role.DisplayName,
    Provider: _provider, Model: model0, ModelVersion: null,
    SessionId: task.UsageSessionId ?? input.RunId),
    usage0, ct);
```

- [ ] **Step 3: Record at the normal completion (after line 226, before the final `return new StepResult`)**

```csharp
var model = role.ModelOverride ?? _defaultModel;
await _usage.RecordAsync(new UsageRecorder.Attribution(
    TenantId: input.TenantId, BoardId: input.BoardId, TaskId: input.TaskId, RunId: input.RunId,
    Step: input.Step, Round: 0, InvocationId: executionContext.InvocationId,
    AgentRoleId: role.Id, AgentRoleName: role.DisplayName,
    Provider: _provider, Model: model, ModelVersion: null,
    SessionId: task.UsageSessionId ?? input.RunId),
    usage, ct);
```

- [ ] **Step 4: Build + suite**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj --nologo` then full suite.
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/workflows/Activities/InvokeAgentActivity.cs
git commit -m "feat(workflows): record usage per pipeline step (incl. NeedsRevision)"
```

---

### Task 12: Capture compaction usage

**Files:**
- Modify: the compaction workflow/activity (find it: `grep -rln "compact" src/workflows`)
- Verification: build + suite.

- [ ] **Step 1: Locate the compaction LLM call**

Run: `grep -rln "compact\|Compact\|Summariz" src/workflows`. The compaction endpoint (`compact/{boardId}/{taskId}`, called from `ChatService.CompactAsync`) runs an LLM summarization. Identify where its token usage is available (the runtime call that produces the summary).

- [ ] **Step 2: Record the summarization usage**

At the point the summary LLM call returns its `TokenUsage`, call `UsageRecorder.RecordAsync` with:
- `AgentRoleId = "system:compaction"`, `AgentRoleName = "Compaction"`,
- `Provider`/`Model` = the task's model (`role.ModelOverride ?? defaultModel`, or the default model if no role context),
- `Step = 0`, `Round = 0`, `InvocationId = ctx.InvocationId`,
- `SessionId = task.UsageSessionId ?? <runId-or-taskId>` — the CURRENT session (compaction does NOT start a new session per spec §5).

Inject `UsageRecorder` into that activity/handler the same way as Tasks 10–11. If compaction currently discards usage, thread it out of the runtime call (the runtime already returns `TokenUsage`).

> If compaction does not currently surface token usage at all (e.g. it uses a raw HTTP call), add usage parsing there mirroring Task 6, or route it through the same runtime method that returns `AgentRunOutcome.TokenUsage`.

- [ ] **Step 3: Build + suite**

Run: `dotnet build src/workflows/TectikaAgents.Workflows.csproj --nologo` then full suite.
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add src/workflows
git commit -m "feat(workflows): track token usage of /compact summarization"
```

---

### Task 13: Session model + `/clear` resets current-session bucket

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/` — the `AgentTask` model (add `UsageSessionId`)
- Modify: `src/api/TectikaAgents.Api/Services/ChatService.cs:137-147` (`ClearAsync`)
- Modify: `ICosmosDbService` + `CosmosDbService` + `InMemoryCosmosDbService` — a rollup session-reset method (api side)
- Test: `tests/TectikaAgents.Tests/ClearSessionTests.cs` (create) — test the pure session-bump + bucket-reset decision if extracted; otherwise verify by build.

- [ ] **Step 1: Add `UsageSessionId` to the task model**

Find the `AgentTask` class (`grep -rln "class AgentTask" src/core`). Add:

```csharp
/// <summary>Identifies the current usage session for the task. Reset (new GUID) on /clear ONLY.
/// New events accrue to the task rollup's currentSession bucket keyed by this id.</summary>
[JsonPropertyName("usageSessionId")]
public string? UsageSessionId { get; set; }
```

- [ ] **Step 2: Implement the api-side rollup session reset**

Add to `ICosmosDbService` and implement in `CosmosDbService` (uses the same ETag pattern as Task 8; the api has its own Cosmos access):

```csharp
// ICosmosDbService
Task ResetTaskUsageSessionAsync(string tenantId, string taskId, string newSessionId, CancellationToken ct = default);
```

```csharp
// CosmosDbService — reset only the currentSession bucket; lifetime/perModel untouched.
public async Task ResetTaskUsageSessionAsync(string tenantId, string taskId, string newSessionId, CancellationToken ct = default)
{
    var id = UsageRollup.TaskId(taskId);
    var container = GetContainer(UsageRollupsContainer);
    var pk = new PartitionKey(tenantId);
    for (var attempt = 0; attempt < 8; attempt++)
    {
        UsageRollup rollup; string? etag = null;
        try { var read = await container.ReadItemAsync<UsageRollup>(id, pk, cancellationToken: ct); rollup = read.Resource; etag = read.ETag; }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return; } // nothing to reset yet
        rollup.CurrentSession = new SessionBucket { SessionId = newSessionId, Since = DateTimeOffset.UtcNow };
        rollup.UpdatedAt = DateTimeOffset.UtcNow;
        try { await container.ReplaceItemAsync(rollup, id, pk, new ItemRequestOptions { IfMatchEtag = etag }, ct); return; }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.PreconditionFailed) { /* retry */ }
    }
}
```

Implement `ResetTaskUsageSessionAsync` in `InMemoryCosmosDbService` as a no-op or simple in-dict reset (match how that fake stores data).

- [ ] **Step 3: Update `ClearAsync` to bump the session + reset the bucket**

In `ChatService.cs` `ClearAsync` (lines 137-147), after stamping `ChatClearedAt`:

```csharp
public async Task<bool> ClearAsync(string boardId, string taskId, CancellationToken ct = default)
{
    var task = await _cosmos.GetTaskAsync(boardId, taskId, ct);
    if (task is null) return false;
    task.FoundryThreadId = null;
    task.TaskBrief = "";
    task.ChatClearedAt = DateTimeOffset.UtcNow;
    var newSessionId = Guid.NewGuid().ToString();
    task.UsageSessionId = newSessionId;          // new session: subsequent events accrue to a fresh bucket
    await _cosmos.UpdateTaskAsync(task, ct);
    await _cosmos.ResetTaskUsageSessionAsync(task.TenantId, taskId, newSessionId, ct);  // reset rollup currentSession bucket
    _logger.LogInformation("[Chat] cleared task {TaskId}", taskId);
    return true;
}
```

> If `ChatService` holds a different field name for tenant (e.g. via the task), use `task.TenantId`. Verify `AgentTask` exposes `TenantId`.

- [ ] **Step 4: Ensure run-start stamps a session if none exists**

A task that has never been cleared has `UsageSessionId == null`. The recorder falls back to `runId`, which would make each run its own session — contradicting "session = since-last-clear". So on run start, if `UsageSessionId` is null, stamp one once. Find the run-start path (`grep -rln "runs/start\|StartRun\|CreateRunAsync" src/api`) and, when creating the first run for a task, set `task.UsageSessionId ??= Guid.NewGuid().ToString()` and persist. Document this in the commit.

- [ ] **Step 5: Build + suite**

Run: `dotnet build` (core, api) then full suite.
Expected: green.

- [ ] **Step 6: Commit**

```bash
git add src/core src/api
git commit -m "feat: usage session model — /clear resets currentSession, run-start stamps a session"
```

---

# Phase 4 — API surface, backfill, seeder

### Task 14: API Cosmos reads for rollups, events, pricing

**Files:**
- Modify: `ICosmosDbService.cs`, `CosmosDbService.cs`, `InMemoryCosmosDbService.cs`
- Verification: build.

- [ ] **Step 1: Add read methods to the interface**

```csharp
Task<UsageRollup?> GetUsageRollupAsync(string tenantId, string id, CancellationToken ct = default);
Task<List<UsageRollup>> GetUsageRollupsForTenantAsync(string tenantId, CancellationToken ct = default);
Task<List<UsageEvent>> GetUsageEventsForTaskAsync(string taskId, int max, string? continuationToken, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `CosmosDbService`**

```csharp
public async Task<UsageRollup?> GetUsageRollupAsync(string tenantId, string id, CancellationToken ct = default)
{
    try { var r = await GetContainer(UsageRollupsContainer).ReadItemAsync<UsageRollup>(id, new PartitionKey(tenantId), cancellationToken: ct); return r.Resource; }
    catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
}

public async Task<List<UsageRollup>> GetUsageRollupsForTenantAsync(string tenantId, CancellationToken ct = default)
{
    var q = new QueryDefinition("SELECT * FROM c WHERE c.tenantId = @t").WithParameter("@t", tenantId);
    var it = GetContainer(UsageRollupsContainer).GetItemQueryIterator<UsageRollup>(q,
        requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(tenantId) });
    var results = new List<UsageRollup>();
    while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
    return results;
}

public async Task<List<UsageEvent>> GetUsageEventsForTaskAsync(string taskId, int max, string? continuationToken, CancellationToken ct = default)
{
    var q = new QueryDefinition("SELECT * FROM c WHERE c.taskId = @t ORDER BY c.timestamp DESC").WithParameter("@t", taskId);
    var it = GetContainer(UsageEventsContainer).GetItemQueryIterator<UsageEvent>(q, continuationToken,
        new QueryRequestOptions { PartitionKey = new PartitionKey(taskId), MaxItemCount = max });
    var results = new List<UsageEvent>();
    if (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));   // one page
    return results;
}
```

> Match the existing query helper style in this file (it has a generic query helper around line 372). Reuse it if cleaner.

- [ ] **Step 3: Implement in `InMemoryCosmosDbService`** (mock mode) — back with in-memory dictionaries mirroring the seeded rollups/events from Task 17.

- [ ] **Step 4: Build**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj --nologo`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/api/TectikaAgents.Api/Services
git commit -m "feat(api): Cosmos reads for usage rollups + events"
```

---

### Task 15: `UsageController`

**Files:**
- Create: `src/api/TectikaAgents.Api/Controllers/UsageController.cs`
- Verification: build + a live smoke (mock mode).

- [ ] **Step 1: Implement the controller (mirror an existing controller's style, e.g. `RunsController`)**

```csharp
using Microsoft.AspNetCore.Mvc;
using TectikaAgents.Api.Services;
using TectikaAgents.Core.Usage;

namespace TectikaAgents.Api.Controllers;

[ApiController]
[Route("api/usage")]
public class UsageController : ControllerBase
{
    private readonly ICosmosDbService _cosmos;
    private readonly CostCalculator _cost;
    private readonly ITenantContext _tenant;   // however tenant id is resolved elsewhere; copy from another controller

    public UsageController(ICosmosDbService cosmos, CostCalculator cost, ITenantContext tenant)
    { _cosmos = cosmos; _cost = cost; _tenant = tenant; }

    [HttpGet("project")]
    public async Task<IActionResult> Project(CancellationToken ct)
    {
        var tenantId = _tenant.TenantId;
        return Ok(await _cosmos.GetUsageRollupAsync(tenantId, UsageRollup.ProjectId(tenantId), ct) ?? EmptyProject(tenantId));
    }

    [HttpGet("board/{boardId}")]
    public async Task<IActionResult> Board(string boardId, CancellationToken ct) =>
        Ok(await _cosmos.GetUsageRollupAsync(_tenant.TenantId, UsageRollup.BoardId(boardId), ct) ?? EmptyBoard(_tenant.TenantId, boardId));

    [HttpGet("task/{taskId}")]
    public async Task<IActionResult> Task(string taskId, CancellationToken ct) =>
        Ok(await _cosmos.GetUsageRollupAsync(_tenant.TenantId, UsageRollup.TaskId(taskId), ct) ?? EmptyTask(_tenant.TenantId, taskId));

    [HttpGet("task/{taskId}/events")]
    public async Task<IActionResult> Events(string taskId, [FromQuery] int max = 50, [FromQuery] string? cursor = null, CancellationToken ct = default) =>
        Ok(await _cosmos.GetUsageEventsForTaskAsync(taskId, Math.Clamp(max, 1, 200), cursor, ct));

    [HttpGet("pricing")]
    public IActionResult Pricing() => Ok(new { version = _cost.CatalogVersion, prices = _cost.Prices });

    private static UsageRollup EmptyProject(string t) => new() { Id = UsageRollup.ProjectId(t), TenantId = t, Scope = UsageScope.Project, ScopeId = t };
    private static UsageRollup EmptyBoard(string t, string b) => new() { Id = UsageRollup.BoardId(b), TenantId = t, Scope = UsageScope.Board, ScopeId = b };
    private static UsageRollup EmptyTask(string t, string k) => new() { Id = UsageRollup.TaskId(k), TenantId = t, Scope = UsageScope.Task, ScopeId = k };
}
```

> Replace `ITenantContext _tenant` with whatever mechanism existing controllers use to resolve tenant id (copy from `RunsController`/`BoardsController`). Register `CostCalculator` as a singleton in the api `Program.cs` too: `builder.Services.AddSingleton(_ => new CostCalculator(PricingCatalogLoader.LoadEmbedded()));`.

- [ ] **Step 2: Build**

Run: `dotnet build src/api/TectikaAgents.Api/TectikaAgents.Api.csproj --nologo`
Expected: succeeds.

- [ ] **Step 3: Live smoke (mock mode)**

Start the api in mock mode and hit the endpoints (use the project's mock-mode launch; see the "running AgentBoard" memory). Expect HTTP 200 and JSON shaped like the rollup/pricing.

```bash
curl -s http://localhost:5000/api/usage/pricing | python3 -m json.tool
curl -s -o /dev/null -w "task usage HTTP %{http_code}\n" http://localhost:5000/api/usage/task/task-impl
```

- [ ] **Step 4: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/UsageController.cs src/api/TectikaAgents.Api/Program.cs
git commit -m "feat(api): UsageController — project/board/task rollups, events, pricing"
```

---

### Task 16: Backfill utility

**Files:**
- Create: `src/api/TectikaAgents.Api/Services/UsageBackfill.cs`
- Verification: build + (optional) one-shot run in mock mode.

- [ ] **Step 1: Implement backfill**

Synthesize project/board/task rollups from existing `WorkflowRun.TotalTokens`, attributed to the default model, so pre-existing data doesn't read as zero. Iterate all runs (reuse an existing "all runs" query or add one), and for each run with `TotalTokens > 0`, apply to project/board/task rollups using `UsageRecorder.ApplyShared`/`ApplyTask` math (or duplicate the bucket math api-side). Mark a `backfilled = true` flag on synthesized rollups (add an optional bool to `UsageRollup` if you want it visible). Make it idempotent: skip if a rollup with non-zero lifetime already exists, or track a `backfillVersion`.

```csharp
// Pseudocode-level structure — fill with the real "list all runs" query + tenant/board resolution:
public sealed class UsageBackfill
{
    private readonly ICosmosDbService _cosmos;
    private readonly CostCalculator _cost;
    public UsageBackfill(ICosmosDbService cosmos, CostCalculator cost) { _cosmos = cosmos; _cost = cost; }

    public async Task RunAsync(string tenantId, CancellationToken ct)
    {
        // 1. Skip if project rollup already has lifetime > 0 (already live or backfilled).
        var existing = await _cosmos.GetUsageRollupAsync(tenantId, UsageRollup.ProjectId(tenantId), ct);
        if (existing is { Lifetime.EventCount: > 0 }) return;

        // 2. For each run, split TotalTokens as input (best-effort: treat all as input=Total, output=0),
        //    compute cost via _cost with the default model, and accumulate into rollups, then persist.
        //    Use UsageRecorder.ApplyShared / ApplyTask for the math; write rollups via a new
        //    ICosmosDbService.UpsertUsageRollupAsync(rollup) added here.
    }
}
```

> Decision: backfill attributes legacy tokens as `input` only (we cannot recover the input/output split from `TotalTokens`). This is explicitly approximate — log it. Add `ICosmosDbService.UpsertUsageRollupAsync(UsageRollup, ct)` for the writes.

- [ ] **Step 2: Wire a trigger** — expose as an admin endpoint `POST /api/usage/backfill` on `UsageController` (guard appropriately) or run on startup once behind a config flag. Prefer the endpoint (explicit, idempotent).

- [ ] **Step 3: Build + (optional) run in mock mode, verify project rollup becomes non-zero.**

- [ ] **Step 4: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/UsageBackfill.cs src/api/TectikaAgents.Api/Controllers/UsageController.cs
git commit -m "feat(api): one-time usage backfill from existing run totals"
```

---

### Task 17: Mock seeder — multi-model usage events + rollups

**Files:**
- Modify: `src/api/TectikaAgents.Api/Services/MockData/MockDataSeeder.cs:288-320`
- Verification: build + mock-mode launch shows realistic per-model data.

- [ ] **Step 1: Seed usage events + rollups across ≥2 models**

Where the seeder currently sets `TotalTokens`/`EstimatedCostUsd` on runs, additionally seed:
- A handful of `UsageEvent`s per seeded task across two models (e.g. `azure-foundry/gpt-4o` and a second seeded model — add a second price row to `pricing-catalog.json` for the seeded model so cost is non-zero, e.g. `azure-foundry/gpt-4o-mini`).
- Corresponding `UsageRollup`s at project/board/task scope computed from those events (reuse `UsageRecorder.ApplyShared`/`ApplyTask`), with a populated `currentSession` on tasks.

Keep the existing `run.TotalTokens`/`EstimatedCostUsd` for back-compat. Persist events/rollups via the in-memory store (mock) and Cosmos (real seed) paths the seeder already uses.

- [ ] **Step 2: Build + mock-mode launch**

Launch in mock mode; confirm `/api/usage/project` and `/api/usage/task/{id}` return non-zero, multi-model data, and the table/panel (after Phase 5) render it.

- [ ] **Step 3: Commit**

```bash
git add src/api/TectikaAgents.Api/Services/MockData/MockDataSeeder.cs src/core/TectikaAgents.Core/Resources/pricing-catalog.json
git commit -m "feat(api): seed realistic multi-model usage events + rollups in mock data"
```

---

# Phase 5 — Web UI

### Task 18: TypeScript types + api client

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts:186-224`
- Modify: `src/web/tectika-board/src/lib/api.ts:131-138`
- Verification: `npm run build`, `npm run lint`.

- [ ] **Step 1: Extend `TokenUsage` and add rollup types in `types.ts`**

```typescript
export interface TokenUsage {
  input: number;
  cachedInput: number;
  output: number;
  reasoning: number;
  total: number;
}

export interface UsageBucket {
  tokens: TokenUsage;
  costUsd: number;
  eventCount: number;
}

export interface SessionBucket extends UsageBucket {
  sessionId: string;
  since: string;
}

export type UsageScope = 'Project' | 'Board' | 'Task';

export interface UsageRollup {
  id: string;
  tenantId: string;
  scope: UsageScope;
  scopeId: string;
  lifetime: UsageBucket;
  perModel: Record<string, UsageBucket>;
  currentSession?: SessionBucket | null;
  updatedAt: string;
}

export interface UsageEvent {
  id: string;
  taskId: string;
  runId: string;
  step: number;
  round: number;
  agentRoleName: string;
  provider: string;
  model: string;
  usage: TokenUsage;
  costUsd: number;
  pricingMissing: boolean;
  currency: string;
  timestamp: string;
}

export interface ModelPrice {
  provider: string;
  model: string;
  modelVersion?: string;
  inputPerMillion: number;
  cachedInputPerMillion: number;
  outputPerMillion: number;
  currency: string;
  effectiveFrom: string;
}

export interface PricingCatalog {
  version: string;
  prices: ModelPrice[];
}
```

- [ ] **Step 2: Add api client methods in `api.ts`**

```typescript
usage: {
  project: () => fetchApi<UsageRollup>('/api/usage/project'),
  board: (boardId: string) => fetchApi<UsageRollup>(`/api/usage/board/${boardId}`),
  task: (taskId: string) => fetchApi<UsageRollup>(`/api/usage/task/${taskId}`),
  events: (taskId: string, max = 50) => fetchApi<UsageEvent[]>(`/api/usage/task/${taskId}/events?max=${max}`),
  pricing: () => fetchApi<PricingCatalog>('/api/usage/pricing'),
},
```

Import the new types where `fetchApi` types are referenced.

- [ ] **Step 3: Verify**

Run: `cd src/web/tectika-board && npm run build && npm run lint`
Expected: build + lint pass. (Existing `TokenUsage` consumers now require `cachedInput`/`reasoning`; the build surfaces any object literals that need updating — fix them by defaulting the new fields to 0.)

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/lib/types.ts src/web/tectika-board/src/lib/api.ts
git commit -m "feat(web): usage rollup/event/pricing types + api client"
```

---

### Task 19: Table tokens/cost columns read `currentSession`

**Files:**
- Modify: `src/web/tectika-board/src/lib/columns.ts:85-93,146-147`
- Modify: `src/web/tectika-board/src/lib/board-context.tsx` (provide per-task usage rollups in cell context)
- Verification: `npm run build` + visual QA.

- [ ] **Step 1: Load task usage rollups into board context**

Where `runsById` is built (board-context.tsx ~lines 260-266, exposed via cell context ~419-422), add a parallel `usageByTaskId: Record<string, UsageRollup>` populated from `api.usage.task(taskId)` for visible tasks (batch alongside the existing run fetches). Expose it on `CellContext`.

- [ ] **Step 2: Point the columns at the rollup's currentSession**

In `columns.ts`, replace the `tokens`/`cost` cases:

```typescript
// cellNumber
case 'tokens': return usageFor(task, ctx)?.currentSession?.tokens.total ?? null;
case 'cost': return usageFor(task, ctx)?.currentSession?.costUsd ?? null;
```

```typescript
// cellText
case 'tokens': { const t = usageFor(task, ctx)?.currentSession?.tokens.total; return t != null ? t.toLocaleString() : ''; }
case 'cost': { const c = usageFor(task, ctx)?.currentSession?.costUsd; return c != null ? `$${c.toFixed(2)}` : ''; }
```

Add the helper near `runFor`:

```typescript
function usageFor(task: AgentTask, ctx: CellContext): UsageRollup | undefined {
  return ctx.usageByTaskId?.[task.id];
}
```

> Decision: table shows **current session** (matches spec). Lifetime is available in the item panel (Task 20).

- [ ] **Step 3: Verify** — `npm run build`; launch mock mode; confirm the tokens/cost columns show seeded current-session values.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/lib/columns.ts src/web/tectika-board/src/lib/board-context.tsx
git commit -m "feat(web): table tokens/cost columns read current-session usage rollup"
```

---

### Task 20: ItemPanel usage panel — session⇄lifetime + per-model

**Files:**
- Create: `src/web/tectika-board/src/components/workspace/UsagePanel.tsx`
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx:297-306` (replace the 3-stat block with the panel)
- Verification: `npm run build` + visual QA.

- [ ] **Step 1: Build the panel**

`UsagePanel.tsx` — fetches `api.usage.task(taskId)`, shows a Session⇄Lifetime toggle and a per-model breakdown table.

```tsx
'use client';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { UsageRollup, UsageBucket } from '@/lib/types';

function fmtCost(c: number) { return `$${c.toFixed(2)}`; }
function fmtTokens(n: number) { return n.toLocaleString(); }

export function UsagePanel({ taskId }: { taskId: string }) {
  const [rollup, setRollup] = useState<UsageRollup | null>(null);
  const [view, setView] = useState<'session' | 'lifetime'>('session');

  useEffect(() => { let on = true; api.usage.task(taskId).then(r => { if (on) setRollup(r); }).catch(() => {}); return () => { on = false; }; }, [taskId]);

  if (!rollup) return null;
  const bucket: UsageBucket | undefined = view === 'session' ? (rollup.currentSession ?? undefined) : rollup.lifetime;
  const tokens = bucket?.tokens.total ?? 0;
  const cost = bucket?.costUsd ?? 0;

  return (
    <div className="rounded-lg border border-[var(--border)] p-3 bg-[var(--background)]">
      <div className="flex items-center justify-between mb-2">
        <span className="font-semibold text-[var(--foreground)]">Usage</span>
        <div className="inline-flex rounded-md border border-[var(--border)] text-xs" role="tablist" aria-label="Usage scope">
          {(['session', 'lifetime'] as const).map(v => (
            <button key={v} role="tab" aria-selected={view === v}
              className={`px-2 py-1 ${view === v ? 'bg-[var(--accent)] text-white' : 'text-[var(--muted)]'}`}
              onClick={() => setView(v)}>{v === 'session' ? 'This session' : 'Task lifetime'}</button>
          ))}
        </div>
      </div>
      <div className="grid grid-cols-2 gap-2 text-center mb-3">
        <Stat label="Tokens" value={fmtTokens(tokens)} />
        <Stat label="Cost" value={fmtCost(cost)} />
      </div>
      <table className="w-full text-xs">
        <thead><tr className="text-[var(--muted)] text-left"><th>Model</th><th className="text-right">Tokens</th><th className="text-right">Cost</th></tr></thead>
        <tbody>
          {Object.entries(rollup.perModel).map(([model, b]) => (
            <tr key={model}><td>{model}</td><td className="text-right">{fmtTokens(b.tokens.total)}</td><td className="text-right">{fmtCost(b.costUsd)}</td></tr>
          ))}
          {Object.keys(rollup.perModel).length === 0 && (<tr><td colSpan={3} className="text-[var(--muted)] py-2">No usage yet</td></tr>)}
        </tbody>
      </table>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (<div><div className="text-[var(--muted)] text-xs">{label}</div><div className="font-semibold">{value}</div></div>);
}
```

> Match the project's existing `Stat` component / styling tokens. If `ItemPanel` already exports a `Stat`, import it instead of redefining.

- [ ] **Step 2: Mount it in `ItemPanel`** — replace the `run`-based Tokens/Cost stat block (lines 297-306) with `<UsagePanel taskId={task.id} />` (keep the Status stat from the run block, or fold it in). The per-model breakdown is the "inspect the whole history of this task" view.

- [ ] **Step 3: Verify** — `npm run build`; mock mode; toggle Session/Lifetime; confirm per-model rows render. Optionally screenshot per the visual-QA memory.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/UsagePanel.tsx src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): task usage panel with session/lifetime toggle + per-model breakdown"
```

---

### Task 21: Dashboards / Analytics read rollups + per-model

**Files:**
- Modify: `src/web/tectika-board/src/app/dashboards/page.tsx:48-72`
- Modify: `src/web/tectika-board/src/app/analytics/page.tsx:36-62`
- Verification: `npm run build` + visual QA.

- [ ] **Step 1: Replace on-the-fly `runs.reduce` sums with the project rollup**

In `dashboards/page.tsx`, fetch `api.usage.project()` and use `rollup.lifetime.costUsd` / `rollup.lifetime.tokens.total` for the "Agent spend" KPI instead of summing runs. Keep run-derived KPIs that aren't cost.

- [ ] **Step 2: Add a per-model cost breakdown** — render `rollup.perModel` as a small table/bar list (model → tokens, cost). This is the headline anti-"misleading aggregate" view.

- [ ] **Step 3: Analytics page** — replace the "Estimated cost" KPI source with the project rollup; add the per-model breakdown there too (or board rollups if the page is board-scoped, via `api.usage.board(boardId)`).

- [ ] **Step 4: Verify** — `npm run build`; mock mode; confirm KPIs + per-model breakdown render with seeded data.

- [ ] **Step 5: Commit**

```bash
git add src/web/tectika-board/src/app/dashboards/page.tsx src/web/tectika-board/src/app/analytics/page.tsx
git commit -m "feat(web): dashboards/analytics read usage rollups + per-model breakdown"
```

---

### Task 22: Read-only pricing catalog view

**Files:**
- Create: `src/web/tectika-board/src/app/settings/pricing/page.tsx` (match the app's existing settings/route structure — `grep -rln "settings" src/web/tectika-board/src/app`)
- Verification: `npm run build` + visual QA.

- [ ] **Step 1: Build the page**

```tsx
'use client';
import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { PricingCatalog } from '@/lib/types';

export default function PricingPage() {
  const [catalog, setCatalog] = useState<PricingCatalog | null>(null);
  useEffect(() => { api.usage.pricing().then(setCatalog).catch(() => {}); }, []);
  if (!catalog) return null;
  return (
    <div className="p-6">
      <h1 className="text-lg font-semibold mb-1">Model pricing</h1>
      <p className="text-[var(--muted)] text-sm mb-4">Catalog version {catalog.version}. Read-only — rates are managed in the repository. Past costs are frozen at the rate in effect when they were incurred.</p>
      <table className="w-full text-sm">
        <thead><tr className="text-left text-[var(--muted)]">
          <th>Provider</th><th>Model</th><th className="text-right">Input /1M</th><th className="text-right">Cached /1M</th><th className="text-right">Output /1M</th><th>Currency</th><th>Effective</th>
        </tr></thead>
        <tbody>
          {catalog.prices.map((p, i) => (
            <tr key={i} className="border-t border-[var(--border)]">
              <td>{p.provider}</td><td>{p.model}{p.modelVersion ? ` (${p.modelVersion})` : ''}</td>
              <td className="text-right">{p.inputPerMillion.toFixed(2)}</td>
              <td className="text-right">{p.cachedInputPerMillion.toFixed(2)}</td>
              <td className="text-right">{p.outputPerMillion.toFixed(2)}</td>
              <td>{p.currency}</td><td>{new Date(p.effectiveFrom).toLocaleDateString()}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
```

- [ ] **Step 2: Link it** — add a nav/settings entry pointing to the page (match existing settings navigation).

- [ ] **Step 3: Verify** — `npm run build`; mock mode; confirm the catalog renders read-only.

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/app/settings/pricing/page.tsx
git commit -m "feat(web): read-only pricing catalog view"
```

---

## Final verification

- [ ] **Full .NET suite:** `dotnet test tests/TectikaAgents.Tests/TectikaAgents.Tests.csproj --nologo` — all green (99 baseline + new tests).
- [ ] **Web:** `cd src/web/tectika-board && npm run build && npm run lint` — pass.
- [ ] **End-to-end (mock mode):** launch api + web; verify (a) table tokens/cost show current-session values, (b) item panel toggles session/lifetime and shows per-model, (c) dashboards/analytics show project totals + per-model, (d) `/api/usage/pricing` renders, (e) clearing a chat resets the table token count while project total is unchanged.
- [ ] **Edge-case spot checks:** stop a run mid-flight (completed-round usage persists, project total unchanged); re-run a task (lifetime grows, current session continues); confirm a second model appears in per-model breakdown.
- [ ] Use superpowers:requesting-code-review before merging.

## Self-review notes (coverage vs spec)

- Spec §3.1 token shape → Task 1, 6. §3.2 pricing catalog + read-only UI → Tasks 2, 15 (`/pricing`), 22. §3.3 usageEvents → Tasks 4, 7, 8, 9. §3.4 rollups → Tasks 5, 8, 9. §4 idempotency/concurrency → Tasks 8 (ETag RMW, 409 dedupe), 9 (invocation-keyed id). §5 edge cases: stop/fail (no decrement — inherent: rollups only ever increment), compact → Task 12, clear → Task 13, retry/concurrency → Tasks 8/9, pricingMissing → Tasks 3/9/20. §6 API → Task 15. §7 UI → Tasks 19–22. §8 backfill/seeder/infra → Tasks 16, 17, 7.
- Session boundary = /clear only (spec §5) → Task 13 (run-start only stamps when null; never re-bumps).
- Known approximation: backfill attributes legacy `TotalTokens` as input-only (Task 16) — logged, not silently wrong.
