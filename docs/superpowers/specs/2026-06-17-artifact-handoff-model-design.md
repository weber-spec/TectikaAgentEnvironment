# Artifact / Handoff Model — Design (Spec 1 of the Repo-Viewer initiative)

- **Date:** 2026-06-17
- **Status:** Draft for review
- **Author:** brainstormed with Claude
- **Scope of this spec:** Generalize the `Artifact` into a project-type-agnostic **handoff model** (a `summary` plus typed `outputs`). This is the foundation that later specs (code viewer, live preview, external-provider integrations) build on.

---

## 1. Context & vision

TectikaAgentEnvironment automates **whole projects** by splitting them into ordered, well-managed tasks executed by agents. The project type is intentionally **unbounded**: a vacation plan (documents), a newspaper (Canva/Figma designs), a big event (bookings, calendars, budgets), a landing page / an API / a code library (a git repo + a running app), and — over time — anything reachable through new service integrations.

The trigger for this work was a requested feature: let the user **see and test the git repository the agents produced, from inside our interface** (the shape proven by Lovable / v0 / bolt.new — a split view with Preview ↔ Code ↔ Console). Designing it surfaced a deeper truth about the system's core data model, which this spec addresses first.

### The core realization: separate the *handoff* from the *product*

Today a task produces a single `Artifact` that fuses two different concerns:

- The **handoff** — a short, human- and agent-readable **summary** of what the task accomplished. It serves the **user** (review) and **downstream tasks** (ingested as `upstreamArtifacts` context). It is small, textual, and universal across every project type.
- The **product** — the actual deliverable. Its form is wildly variable and **frequently lives outside our system**: a git branch/PR on GitHub, a Canva design, a Google Doc, a deployed URL, a calendar booking.

In the trip-planner board these coincided (the plan document was both the handoff and the product), which hid the distinction. For most real project types they do **not** coincide — the product can't be the raw stored content; it must be an **explanation/summary plus a reference to where the product actually lives**.

### Roadmap (this spec is step 1)

1. **Spec 1 (this doc): the general handoff model** — `Artifact = summary + outputs[]`, where each output is inline content or an external reference. Wire up the **Document** output kind (re-expressing today's behavior). Back-compat for existing boards. _No git, no UI repo browser yet._
2. **Spec 2: the code viewer + `Code` output kind** — a GitHub read service, a board-level repo browser (file tree, viewer, history, PRs), a task-level branch diff in the right-hand pane, and `Code` output enrichment from git (branch, commit, changed files, PR). Both homes: **board = the whole product**, **task = what one agent changed**.
3. **Later phases:** live preview (build + run + shareable URL, the breakthrough; constrained by today's ephemeral ACI workspaces), an interactive console, and additional external providers (Canva, Figma, Google Drive, deployments) — all reusing the `external` output shape with **no model rework**.

---

## 2. Problem statement

The current `Artifact` (`src/core/TectikaAgents.Core/Models/Artifact.cs`) holds a single `Content` string + `ContentType` and an optional `Summary`. This:

- cannot represent a product that lives in an external system of record (git, Canva, a URL);
- cannot represent **multiple** deliverables from one task;
- forces downstream tasks to ingest the **full** content (noisy and expensive) because there is no first-class, always-present summary;
- has no clean place for the upcoming code viewer to attach a git reference.

---

## 3. The general model

An **Artifact** is the task's **handoff**: a required `summary` plus zero-or-more typed `outputs`. Each output is either **inline** (stored in Cosmos) or **external** (a pointer to a system of record).

```
Artifact {
  id, taskId, runId, version            // unchanged
  summary: string                        // ALWAYS present — the handoff narrative
  outputs: Output[]                      // 0..n products this task produced
  inputContext, origin, internalLogs     // unchanged
}

Output {
  id: string
  kind: OutputKind                       // Document | Code | Design | Dataset | Deployment | Link (extensible)
  label?: string                         // short human label, e.g. "Checkout branch", "Front page"
  inline?:   { contentType, content }    // small products stored in Cosmos (today's doc artifacts)
  external?: { provider, locator, previewUrl? }  // products living elsewhere
}
```

- Exactly one of `inline` / `external` is set per output.
- `provider` is a string (`"github"`, `"canva"`, `"figma"`, `"googledocs"`, `"deployment"`, …). `locator` is a provider-specific object (e.g. for git in Spec 2: `{ branch, baseBranch, commitSha, changedFiles[], prNumber? }`).
- **In Spec 1 we implement the `Document` kind only** (inline). `Code` and other kinds are defined in the schema/enum so the shape is forward-compatible, but their production and rendering land in later specs.

### Confirmed design decisions (from brainstorming)

- **Multiple outputs per task:** yes — `outputs` is a list.
- **Summary is always a distinct field:** yes — deciding per-task whether to have a summary is redundant and adds no benefit; it is always present.
- **Git is not special:** it is simply the first `external` provider; all future integrations reuse the `external` shape.

---

## 4. Agent finalization contract: two explicit steps

Outputs must be a **deliberate act by the agent**, not inferred from tool history. Agents call tools to explore, to fix blockers, and to recover from mistakes; **those side effects are not deliverables.** Inferring outputs from tool calls would be unstable and wrong (explicitly raised and rejected during brainstorming).

Therefore finalization is **two separate, auditable steps**, isolated from the working tool-loop:

1. **Declare outputs.** The agent *intentionally* registers each deliverable through a dedicated `declare_output` capability (a tool/structured action), distinct from ordinary work tools. Each declaration: `{ kind, label, inline content | external reference }`. The agent chooses what counts as a product. For `external` references, the system **enriches** the declaration with verified metadata at declaration time (in Spec 2, e.g. resolving a git branch to its commit SHA + changed files via the GitHub read service). A task that only did exploratory/fix-up work declares **no** outputs.
2. **Write the summary.** A separate short handoff narrative.

**Why two steps:** auditability (you can see exactly what the agent chose to publish, independent of *how* it got there) and isolation (outputs are decoupled from incidental tool use).

**Outputs are editable within a session.** Declarations are *not* write-once — work frequently changes mid-session, so the agent must be able to revise what it has published. Each output is addressable by its `id`, and the agent has companion capabilities to **update** an existing output (change its `label`, inline content, or external reference) and to **remove** one it no longer considers a deliverable (e.g. `update_output` / `remove_output`, or a single upsert-by-id plus a remove — exact shape settled in the implementation plan). Edits are themselves auditable: every declare/update/remove is recorded in the run trace, so the history of *what was published and when* stays inspectable even as the current set changes.

**Runtime touch-points** (`src/workflows/Activities/RunAgentRoundActivity.cs`, `src/agentruntime/`): the round outcome is extended from a single `FinalText` to `{ summary, outputs[] }`, where `outputs[]` is the agent's current declared set after any in-session edits. Agent system prompts gain instructions for the two-step finalization, the `declare_output` capability, and editing previously declared outputs. Versioning semantics: each finalization persists a new `Artifact` version capturing the *current* output set, so each version is a coherent snapshot; the latest version is the task's current handoff, and earlier versions preserve the trail of how the deliverables evolved.

---

## 5. Backward compatibility (non-destructive)

Existing boards (e.g. the trip-planner) have artifacts with `Content` + `ContentType` and no `outputs`. We do **not** run a destructive migration:

- A **read-time shim** presents any legacy artifact as a single inline **Document** output (`{ inline: { contentType: ContentType, content: Content } }`), with `summary` falling back to the existing `Summary` (or the first lines of `Content` when `Summary` is empty).
- Legacy fields (`Content`, `ContentType`, `Summary`) are retained on the model for read compatibility; **new** writes populate `summary` + `outputs[]`.
- Existing document-producing agent roles get prompt updates so new runs declare a Document output + a summary via the two-step contract.

---

## 6. Downstream consumption

Downstream tasks currently ingest the full artifact content. After this change they ingest the **`summary` plus lightweight output references** (e.g. labels and, for external outputs, their locator/preview — "code at PR #12") rather than raw product bodies. This reduces context noise and cost for every project type. Inline content remains available on demand when a downstream task genuinely needs the full body.

(`upstreamArtifacts` wiring in `RunAgentRoundActivity` / the orchestrator is updated to pass summary + refs.)

---

## 7. UI

The task `ItemPanel` right-hand pane (`src/web/tectika-board/src/components/workspace/ItemPanel.tsx`, today's `ArtifactPane`) becomes **output-kind-aware**:

- It renders the `summary` prominently, then renders each output by `kind`.
- **Spec 1 implements the `Document` renderer** (markdown/text/json — matching today's look). Other kinds (Code → diff, Design → embed, Link → card) are stubs/"coming soon" until their specs.
- The placement decision from brainstorming (the task-level code view lives on the **right**, replacing the document render for code tasks) is honored by this same pane in Spec 2 — the right pane simply renders a `Code` output instead of a `Document` one.

TypeScript types in `src/web/tectika-board/src/lib/types.ts` mirror the new `Artifact` / `Output` shape.

---

## 8. Data flow (Spec 1, Document task)

1. Agent works the task via tools (no automatic output capture).
2. **Finalize step 1:** agent calls `declare_output` with an inline Document deliverable.
3. **Finalize step 2:** agent writes the `summary`.
4. `RunAgentRoundActivity` persists a new `Artifact { summary, outputs:[Document] }` version in Cosmos.
5. Downstream tasks receive `summary` + output refs.
6. The right pane renders the summary + the Document output.

---

## 9. Error handling / edge cases

- **No outputs declared:** valid; the pane shows the summary and a "no deliverables" empty state.
- **Malformed declaration** (both/neither inline & external, unknown kind): rejected at persistence with a clear error; surfaced in the run trace.
- **Legacy artifact with empty `Summary`:** shim derives a summary from the content's first lines.
- **Large inline content:** unchanged from today (Cosmos document-size limits apply); large/external products are the reason the `external` path exists and is preferred in later specs.

---

## 10. Scope

**In scope (Spec 1):**
- New `Artifact` shape (`summary` required, `outputs[]`) + `Output` model + `OutputKind` enum, in `src/core`.
- TypeScript mirror in the web app.
- `Document` output kind: production (two-step finalization) + rendering.
- Two-step agent finalization contract (`declare_output` + summary) in the agent runtime / round activity, plus prompt updates.
- Non-destructive read-time back-compat shim for legacy artifacts.
- Downstream consumption uses summary + refs.
- Right-pane renders summary + Document outputs.
- Tests (see §11).

**Out of scope (later specs):**
- `Code` output enrichment, the GitHub read service, the board-level repo browser, task-level diffs (**Spec 2**).
- `WorkflowRun` branch/push/commit fields (**Spec 2** — they are git-specific).
- Live preview / build-and-run environments, interactive console (**later phases**).
- External providers beyond the schema stub: Canva, Figma, Google Drive, deployments (**future**).

---

## 11. Testing

- **Unit (core):** `Output` validation (exactly one of inline/external; valid kind); `Artifact` requires `summary`.
- **Unit (shim):** legacy artifact → single Document output; summary fallback behavior.
- **Runtime:** round finalization produces `{ summary, outputs }`; `declare_output` declarations map to `Output` records; a no-output round yields an empty `outputs` list.
- **Downstream:** upstream context contains summary + refs, not raw bodies.
- **API contract:** artifact endpoints serialize the new shape and the shimmed legacy shape identically.
- **Frontend:** right pane renders summary + Document output; empty/"no deliverables" state; legacy artifact renders unchanged.

---

## 12. Open questions

- Exact wording/shape of the `declare_output` capability as exposed to agents (tool schema vs. structured final message) — to be settled in the implementation plan, consistent with the existing tool/loop conventions in `src/agentruntime`.
- Whether `summary` should have a soft length budget to keep downstream context cheap (proposed: yes, a guideline in the prompt rather than a hard cap).
