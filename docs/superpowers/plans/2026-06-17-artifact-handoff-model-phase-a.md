# Artifact Handoff Model — Phase A (Data Model + Back-Compat + Read/Render) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generalize `Artifact` into a handoff (`summary` + typed `outputs[]`) and make the read/render path serve every artifact through the new shape — including a non-destructive back-compat shim for existing artifacts — without touching the agent loop.

**Architecture:** Add an `Output` model (inline content or external reference) and an `Outputs` list + a `summary` to `Artifact`. A pure `EnsureHandoffShape()` normalizer derives a single `Document` output + a summary from legacy `Content`/`ContentType` on read. The API read endpoint applies the normalizer so the frontend always receives the new shape. The web `ArtifactBody` renders `summary` then each output by kind (Document now; other kinds show a "coming soon" placeholder). No changes to artifact *production* (that is Phase B).

**Tech Stack:** C# / .NET 10, xUnit (`tests/TectikaAgents.Tests`), System.Text.Json (Cosmos serializer), Next.js 16 / React 19 / TypeScript / Tailwind v4, `node --test`.

**Scope note:** This is Plan A of two for Spec 1 (`docs/superpowers/specs/2026-06-17-artifact-handoff-model-design.md`). Plan B (agent finalization write-path: `declare/update/remove_output`, round-outcome `{summary, outputs}`, downstream consumption of summary+refs, prompt updates) is a separate follow-up plan that builds on the types created here.

---

## File Structure

**Create:**
- `src/core/TectikaAgents.Core/Models/Output.cs` — `OutputKind`, `InlineContent`, `ExternalRef`, `Output` (the typed-product value objects).
- `tests/TectikaAgents.Tests/OutputTests.cs` — `Output.IsValid()` rules.
- `tests/TectikaAgents.Tests/ArtifactHandoffShapeTests.cs` — `EnsureHandoffShape()` back-compat behavior.

**Modify:**
- `src/core/TectikaAgents.Core/Models/Artifact.cs` — add `Outputs` list + `EnsureHandoffShape()` + `DeriveSummary()`.
- `src/api/TectikaAgents.Api/Controllers/ArtifactsController.cs:18-22` — normalize versions on read.
- `src/web/tectika-board/src/lib/types.ts:9-10,216-230` — add `OutputKind`/`InlineContent`/`ExternalRef`/`Output`; add `outputs` to `Artifact`.
- `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` (the `ArtifactBody` function, ~`828-845`) — render `summary` + `outputs[]` by kind.

---

## Task 1: `Output` value objects (core)

**Files:**
- Create: `src/core/TectikaAgents.Core/Models/Output.cs`
- Test: `tests/TectikaAgents.Tests/OutputTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/OutputTests.cs`:

```csharp
using TectikaAgents.Core.Models;
using Xunit;

public class OutputTests
{
    [Fact]
    public void Valid_WhenOnlyInlineSet()
    {
        var o = new Output { Kind = OutputKind.Document, Inline = new InlineContent { Content = "hi" } };
        Assert.True(o.IsValid());
    }

    [Fact]
    public void Valid_WhenOnlyExternalSet()
    {
        var o = new Output { Kind = OutputKind.Code, External = new ExternalRef { Provider = "github" } };
        Assert.True(o.IsValid());
    }

    [Fact]
    public void Invalid_WhenBothSet()
    {
        var o = new Output { Inline = new InlineContent(), External = new ExternalRef() };
        Assert.False(o.IsValid());
    }

    [Fact]
    public void Invalid_WhenNeitherSet()
    {
        var o = new Output();
        Assert.False(o.IsValid());
    }

    [Fact]
    public void Output_HasGeneratedIdByDefault()
    {
        var o = new Output();
        Assert.False(string.IsNullOrWhiteSpace(o.Id));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~OutputTests`
Expected: FAIL — build error, `Output` / `OutputKind` / `InlineContent` / `ExternalRef` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/core/TectikaAgents.Core/Models/Output.cs`:

```csharp
using System.Text.Json.Serialization;

namespace TectikaAgents.Core.Models;

/// <summary>The kind of product a task output represents. Forward-compatible:
/// only <see cref="Document"/> is produced/rendered in Phase A; the rest are
/// wired up in later specs (Code in Spec 2; Design/Dataset/Deployment/Link beyond).</summary>
public enum OutputKind { Document, Code, Design, Dataset, Deployment, Link }

/// <summary>A product stored directly in Cosmos (small enough to inline).</summary>
public sealed class InlineContent
{
    [JsonPropertyName("contentType")]
    public ArtifactContentType ContentType { get; set; } = ArtifactContentType.Markdown;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>A pointer to a product that lives in an external system of record
/// (git, Canva, a deployment URL, …). <see cref="Locator"/> is provider-specific.</summary>
public sealed class ExternalRef
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("locator")]
    public Dictionary<string, object?> Locator { get; set; } = new();

    [JsonPropertyName("previewUrl")]
    public string? PreviewUrl { get; set; }
}

/// <summary>One deliverable produced by a task. Exactly one of <see cref="Inline"/>
/// or <see cref="External"/> is set.</summary>
public sealed class Output
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("kind")]
    public OutputKind Kind { get; set; } = OutputKind.Document;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("inline")]
    public InlineContent? Inline { get; set; }

    [JsonPropertyName("external")]
    public ExternalRef? External { get; set; }

    /// <summary>True when exactly one of inline / external is populated.</summary>
    public bool IsValid() => (Inline is null) ^ (External is null);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~OutputTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/Output.cs tests/TectikaAgents.Tests/OutputTests.cs
git commit -m "feat(core): add Output value objects (inline | external) for handoff model"
```

---

## Task 2: `Artifact` gains `Outputs` + `EnsureHandoffShape()` back-compat

**Files:**
- Modify: `src/core/TectikaAgents.Core/Models/Artifact.cs`
- Test: `tests/TectikaAgents.Tests/ArtifactHandoffShapeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/TectikaAgents.Tests/ArtifactHandoffShapeTests.cs`:

```csharp
using TectikaAgents.Core.Models;
using Xunit;

public class ArtifactHandoffShapeTests
{
    [Fact]
    public void Legacy_DerivesSingleDocumentOutput()
    {
        var a = new Artifact { ContentType = ArtifactContentType.Markdown, Content = "## Plan\nDay 1: arrive" };

        a.EnsureHandoffShape();

        Assert.Single(a.Outputs);
        var o = a.Outputs[0];
        Assert.Equal(OutputKind.Document, o.Kind);
        Assert.NotNull(o.Inline);
        Assert.Equal(ArtifactContentType.Markdown, o.Inline!.ContentType);
        Assert.Equal("## Plan\nDay 1: arrive", o.Inline.Content);
    }

    [Fact]
    public void Legacy_DerivesSummaryFromFirstMeaningfulLine()
    {
        var a = new Artifact { Content = "## Plan\nDay 1: arrive", Summary = null };

        a.EnsureHandoffShape();

        Assert.Equal("Plan", a.Summary);
    }

    [Fact]
    public void Legacy_KeepsExistingSummaryWhenPresent()
    {
        var a = new Artifact { Content = "## Plan", Summary = "Trip itinerary" };

        a.EnsureHandoffShape();

        Assert.Equal("Trip itinerary", a.Summary);
    }

    [Fact]
    public void New_ArtifactWithOutputsIsLeftUnchanged()
    {
        var a = new Artifact
        {
            Summary = "Added checkout",
            Outputs = [new Output { Kind = OutputKind.Document, Inline = new InlineContent { Content = "body" } }],
        };

        a.EnsureHandoffShape();

        Assert.Single(a.Outputs);
        Assert.Equal("Added checkout", a.Summary);
    }

    [Fact]
    public void Empty_ArtifactProducesNoOutputsAndEmptySummary()
    {
        var a = new Artifact { Content = "", Summary = null };

        a.EnsureHandoffShape();

        Assert.Empty(a.Outputs);
        Assert.Equal("", a.Summary);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~ArtifactHandoffShapeTests`
Expected: FAIL — `Outputs` property and `EnsureHandoffShape()` do not exist.

- [ ] **Step 3: Write minimal implementation**

In `src/core/TectikaAgents.Core/Models/Artifact.cs`, add the `Outputs` property immediately after the `Summary` property:

```csharp
    [JsonPropertyName("outputs")]
    public List<Output> Outputs { get; set; } = [];
```

Then add these two methods inside the `Artifact` class (after the last property, before the closing brace):

```csharp
    /// <summary>Non-destructive read-time normalizer: any legacy artifact (populated
    /// <see cref="Content"/>, empty <see cref="Outputs"/>) is presented as a single
    /// inline Document output, and a missing <see cref="Summary"/> is derived from the
    /// content. New artifacts (with outputs) are returned unchanged.</summary>
    public Artifact EnsureHandoffShape()
    {
        if (Outputs.Count == 0 && !string.IsNullOrEmpty(Content))
        {
            Outputs = [new Output
            {
                Kind = OutputKind.Document,
                Inline = new InlineContent { ContentType = ContentType, Content = Content },
            }];
        }

        if (string.IsNullOrWhiteSpace(Summary))
            Summary = DeriveSummary(Content);

        return this;
    }

    /// <summary>First meaningful line of markdown content, stripped of leading
    /// heading hashes / list markers and truncated, for use as a fallback summary.</summary>
    internal static string DeriveSummary(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var line = content
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
        line = line.TrimStart('#', '-', '*', '>', ' ').Trim();
        return line.Length > 200 ? line[..200].TrimEnd() + "…" : line;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/TectikaAgents.Tests --filter FullyQualifiedName~ArtifactHandoffShapeTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/core/TectikaAgents.Core/Models/Artifact.cs tests/TectikaAgents.Tests/ArtifactHandoffShapeTests.cs
git commit -m "feat(core): add Artifact.Outputs + EnsureHandoffShape back-compat normalizer"
```

---

## Task 3: API serves normalized handoff shape on read

**Files:**
- Modify: `src/api/TectikaAgents.Api/Controllers/ArtifactsController.cs`

This task has no new unit test (the normalizer logic is covered by Task 2; the controller change is a one-line mapping verified by build + the smoke check below).

- [ ] **Step 1: Apply the normalizer in `GetVersions`**

In `src/api/TectikaAgents.Api/Controllers/ArtifactsController.cs`, replace the `GetVersions` method body:

```csharp
    /// <summary>All versions of a task's artifact, newest first, normalized to the handoff shape.</summary>
    [HttpGet("{taskId}")]
    public async Task<IActionResult> GetVersions(string taskId, CancellationToken ct)
    {
        var versions = await _cosmos.GetArtifactVersionsAsync(taskId, ct);
        var shaped = versions.Select(a => a.EnsureHandoffShape()).ToList();
        return Ok(shaped);
    }
```

- [ ] **Step 2: Build the API to verify it compiles**

Run: `dotnet build src/api/TectikaAgents.Api`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full backend test suite (no regressions)**

Run: `dotnet test tests/TectikaAgents.Tests`
Expected: PASS — all existing tests plus the new Output/ArtifactHandoffShape tests.

- [ ] **Step 4: Commit**

```bash
git add src/api/TectikaAgents.Api/Controllers/ArtifactsController.cs
git commit -m "feat(api): normalize artifact versions to handoff shape on read"
```

---

## Task 4: Frontend types mirror the handoff shape

**Files:**
- Modify: `src/web/tectika-board/src/lib/types.ts`

- [ ] **Step 1: Add the output types**

In `src/web/tectika-board/src/lib/types.ts`, immediately after the existing line `export type ArtifactOrigin = 'Agent' | 'HumanEdit' | 'CliBridge';` add:

```typescript
export type OutputKind = 'Document' | 'Code' | 'Design' | 'Dataset' | 'Deployment' | 'Link';

export interface InlineContent {
  contentType: ArtifactContentType;
  content: string;
}

export interface ExternalRef {
  provider: string;
  locator: Record<string, unknown>;
  previewUrl?: string;
}

export interface Output {
  id: string;
  kind: OutputKind;
  label?: string;
  inline?: InlineContent;
  external?: ExternalRef;
}
```

- [ ] **Step 2: Add `outputs` to the `Artifact` interface**

In the same file, in the `export interface Artifact { … }` block, add this line immediately after `summary?: string;`:

```typescript
  outputs: Output[];
```

- [ ] **Step 3: Type-check**

Run: `cd src/web/tectika-board && npx tsc --noEmit`
Expected: no errors (the new optional/array fields don't break existing usage; `ArtifactBody` is updated in Task 5).

- [ ] **Step 4: Commit**

```bash
git add src/web/tectika-board/src/lib/types.ts
git commit -m "feat(web): mirror Output/handoff types for Artifact"
```

---

## Task 5: `ArtifactBody` renders summary + outputs by kind

**Files:**
- Modify: `src/web/tectika-board/src/components/workspace/ItemPanel.tsx` (the `ArtifactBody` function)

- [ ] **Step 1: Replace the `ArtifactBody` render**

In `src/web/tectika-board/src/components/workspace/ItemPanel.tsx`, replace the entire `ArtifactBody` function with:

```tsx
function ArtifactBody({ artifact }: { artifact: Artifact }) {
  // API normalizes to the handoff shape; fall back to legacy content defensively.
  const outputs = artifact.outputs && artifact.outputs.length > 0
    ? artifact.outputs
    : [{ id: 'legacy', kind: 'Document' as const, inline: { contentType: artifact.contentType, content: artifact.content } }];

  return (
    <div>
      {artifact.inputContext.upstreamArtifacts.length > 0 && (
        <div className="mb-3 text-[11px] text-[var(--muted)]">
          <span className="font-semibold">Input context:</span> {artifact.inputContext.upstreamArtifacts.map(u => `${u.contentType} from ${u.taskId} v${u.version}`).join(', ')}
          {artifact.inputContext.humanContext && <div className="italic mt-1">“{artifact.inputContext.humanContext}”</div>}
        </div>
      )}

      {artifact.summary && (
        <div className="mb-3">
          <div className="uppercase tracking-wide text-[var(--muted)] font-semibold text-[11px] mb-1">Summary</div>
          <div className="text-[13px] text-[var(--foreground)]">{artifact.summary}</div>
        </div>
      )}

      {outputs.map(o => <OutputView key={o.id} output={o} />)}

      {artifact.internalLogs.length > 0 && (
        <div className="mt-3 text-[11px]">
          <div className="uppercase tracking-wide text-[var(--muted)] font-semibold mb-1">Execution log</div>
          {artifact.internalLogs.map((l, i) => <div key={i} className="text-[var(--muted)] font-mono">› {l}</div>)}
        </div>
      )}
    </div>
  );
}

function OutputView({ output }: { output: Output }) {
  if (output.kind === 'Document' && output.inline) {
    return output.inline.contentType === 'Markdown'
      ? <Markdown text={output.inline.content} />
      : <pre className="font-mono text-[12.5px] bg-[var(--background)] border border-[var(--border)] rounded-lg p-3 overflow-auto whitespace-pre-wrap text-[var(--foreground)]">{output.inline.content}</pre>;
  }
  // Non-Document kinds (Code, Design, …) are produced/rendered in later specs.
  return (
    <div className="border border-dashed border-[var(--border)] rounded-lg p-3 text-[12px] text-[var(--muted)]">
      <span className="font-semibold text-[var(--foreground)]">{output.label ?? output.kind}</span> — {output.kind} output rendering coming soon.
    </div>
  );
}
```

- [ ] **Step 2: Add the `Output` import**

In the same file, find the type import line that includes `Artifact` from `@/lib/types` (e.g. `import type { Artifact, AgentTask, AgentRole, RunEvent, HumanInteraction } from '@/lib/types';`) and add `Output` to it:

```tsx
import type { Artifact, AgentTask, AgentRole, RunEvent, HumanInteraction, Output } from '@/lib/types';
```

- [ ] **Step 3: Type-check and lint**

Run: `cd src/web/tectika-board && npx tsc --noEmit && npm run lint`
Expected: no type errors, no new lint errors.

- [ ] **Step 4: Build**

Run: `cd src/web/tectika-board && npm run build`
Expected: build succeeds.

- [ ] **Step 5: Manual render check**

Start the app and the mock DB per the project's AgentBoard QA flow, open a board that has an existing (legacy) artifact, open a task's right-hand "Evolving Artifact" pane, and confirm: the **Summary** block shows, the document body renders exactly as before, and no console errors. (Legacy artifacts now flow through `outputs` via the API normalizer.)

- [ ] **Step 6: Commit**

```bash
git add src/web/tectika-board/src/components/workspace/ItemPanel.tsx
git commit -m "feat(web): render artifact summary + outputs by kind in the right pane"
```

---

## Self-Review

**Spec coverage (against `2026-06-17-artifact-handoff-model-design.md`):**
- §3 model (`summary` + `outputs[]`, inline|external, kinds) → Tasks 1, 2, 4. ✓ (`summary` already existed on `Artifact`; this plan makes it always-populated on read via the normalizer.)
- §5 non-destructive back-compat shim → Task 2 (`EnsureHandoffShape`) + Task 3 (applied on read). ✓
- §7 UI right pane renders summary + outputs by kind (Document now) → Task 5. ✓
- §10 scope: Document kind only; Code/external defined but not produced/rendered → enum + placeholder in Task 5. ✓
- §4 two-step finalization, §6 downstream consumption, `WorkflowRun` fields → **deferred to Plan B** (declared in Scope note). Not a gap.

**Placeholder scan:** No "TBD"/"handle edge cases"/uncoded steps — every code step shows full code. ✓

**Type consistency:** `Output`, `OutputKind`, `InlineContent`, `ExternalRef` identical across C# (Tasks 1–2) and TS (Task 4); `EnsureHandoffShape()` defined in Task 2 and called in Task 3; `OutputView`/`Output` used in Task 5 match Task 4's TS interface. ✓
