# Foundry model list — design

**Date:** 2026-06-22
**Status:** Approved (design); pending implementation plan

## Problem

The "new agent" modal's model `<select>` is populated from a hardcoded array
(`agents/page.tsx:12`), and the in-task agent config has a second identical
hardcoded array (`ItemPanel.tsx:26`). Neither reflects the models actually
deployed in the Foundry project. We want the picker to show the **live list of
available models from Foundry**.

## Goals

- The model picker lists the models actually available in the Foundry project.
- Local/dev (mock mode) keeps working with a representative static list.
- Both the new-agent modal and the in-task ItemPanel picker use the same source.
- Editing an existing agent never silently drops its saved model.

## Non-goals

- Rich model metadata / capability badges in the UI (names only for now).
- A static fallback list when a *real* Foundry fetch fails (deliberately not
  done — we surface the error instead; see Error handling).
- Per-tenant model scoping (the Foundry project is single per deployment).

## Architecture

### Backend — `IModelCatalog` (mirrors the `IAgentProvisioner` pattern)

New interface in `TectikaAgents.Core`:

```csharp
public interface IModelCatalog
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}
```

Returns model/deployment **names** — the exact strings that get written to
`AgentRole.ModelOverride` and passed to Foundry as the agent definition `model`.

Two implementations, registered by the **same `Foundry:UseMock` flag** that
selects `IAgentProvisioner` (`Program.cs`), in both the API and (for symmetry,
if needed) workflows host:

- **`MockModelCatalog`** — returns the canonical static list:
  `["gpt-4o", "claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5", "o3"]`.
  This is now the single source of the dev/mock list, replacing the two
  frontend constants.
- **`FoundryModelCatalog`** (in `src/agentruntime`, beside `FoundryAgentRuntime`)
  — enumerates the Foundry project's **model deployments**, filters to
  chat/text-completion-capable models, and caches the result in-memory with a
  short TTL (~5 min) to avoid hitting Foundry on every modal open. On failure it
  **propagates the exception** (no static fallback).

#### Foundry enumeration — to be pinned during implementation

`FoundryModelCatalog` reuses the existing Foundry access pattern:
`DefaultAzureCredential` (scope `https://ai.azure.com/.default`) + a bearer
`HttpClient` against `FoundrySettings.ProjectEndpoint`
(`https://<sub>.services.ai.azure.com/api/projects/<project>`), the same base
and `api-version=v1` style `FoundryAgentRuntime` already uses.

**Open item:** the exact deployments call (path + api-version + response shape)
must be confirmed. Plan: inspect the restored `Azure.AI.Projects` (1.0.0-beta.9)
SDK surface (its `Deployments` operations / `ModelDeployment` shape) and the
documented data-plane `GET {ProjectEndpoint}/deployments` contract, then
implement whichever is correct. The JSON parser will be defensive about
container shape (`value`/`data`) and tolerant of unknown fields. This is the one
piece that **cannot be fully verified from the dev box** (no Foundry creds; the
project is in swedencentral) — the live call needs a credentialed environment to
confirm; the mock path and parser are unit-tested here.

#### Filtering

List all model deployments, filtering to chat/text-completion-capable models
(exclude embeddings/vision-only) where the deployment metadata allows
classification. Deployments that can't be classified are **listed, not hidden**.

### API — `ModelsController`

```
GET /api/models        [Authorize]
  200 -> string[]       (the catalog list)
  502 -> problem        (real-mode Foundry fetch failed)
```

Injects `IModelCatalog`. In mock mode it returns the static list and never 502s.

### Frontend — shared `ModelSelect`

- `api.models.list(): Promise<string[]>` in `lib/api.ts`.
- A small shared `ModelSelect` component replaces the duplicated `MODELS`
  constants in `agents/page.tsx` (RoleEditor) and `ItemPanel.tsx`. It fetches on
  mount and renders three states:
  - **loading** — disabled, "Loading models…".
  - **success** — the live list; always includes a **"Default"** option (empty
    `modelOverride` ⇒ use the project default model), and **guarantees the
    currently-saved value is present** as an option even if it is no longer in
    the live list.
  - **error** — inline "Couldn't load models from Foundry"; the select offers
    only Default + the currently-saved value, so the form stays submittable.

The "Default" sentinel (empty string) is always present regardless of fetch
state, since it is not a Foundry model.

## Error handling

| Situation                          | Behavior                                             |
|------------------------------------|------------------------------------------------------|
| Mock mode                          | Static list, always 200.                             |
| Real mode, Foundry OK              | Live deployment list (cached ~5 min).                |
| Real mode, Foundry fetch fails     | API 502; UI shows error, offers Default + saved value.|
| Saved model not in live list       | Still shown as a selectable option (never dropped).  |

## Testing

- **Backend:** unit-test `FoundryModelCatalog`'s deployment-JSON parsing against
  a fixture (incl. an embedding deployment that gets filtered); `MockModelCatalog`;
  `ModelsController` success (mock) and error (catalog throws) paths.
- **Frontend:** `ModelSelect` loading / success / error states and the
  saved-value-preserved behavior; `api.models.list` client method.

## Rollout / verification

- Mock path + all UI states: verified locally (mock API + Next dev + Playwright).
- Real Foundry path: deploy/credentialed verification required; flagged above.
