# Live Preview — Design Spec

**Date:** 2026-06-22
**Status:** Approved (brainstorm complete)
**Branch/worktree:** `feat/live-preview` (worktree off `origin/main`)

## Summary

Let a user **run and click through the agent-built web app from inside the product**. From
the board-level Repo view, the user picks a branch and clicks **Start preview**; the platform
provisions a dedicated, ephemeral Azure Container Instance (ACI) that clones the branch,
installs dependencies, and runs the app on a public URL. The preview auto-idles and tears
itself down to bound cost.

This is the third layer of the code initiative: **Code view** (shipped) → **Live Preview**
(this spec) → agent-declared run commands (later).

## Cornerstone decisions (from brainstorm)

| Decision | Choice |
| --- | --- |
| Scope | **Runnable web apps only.** Non-web projects show a "not previewable" state. |
| Lifecycle | **On-demand + auto-idle.** Pay only while viewing. |
| Placement | **Board Repo view**, previews the **currently-selected branch** (covers `main` and any `agent/*` branch via the existing branch switcher). |
| Access | **Public unguessable URL** (random DNS label). Shareable; no login wall on the served app. |
| Hosting | **Dedicated ACI per preview** (Approach 1), modeled on the existing run-workspace ACI pattern. |

## Architecture

```
Repo view ──(pick branch, Start)──▶ API: PreviewController
                                       │
                                       ▼
                              PreviewService (single writer of session state)
                                       │  writes PreviewSession (Cosmos: previewSessions)
                                       ▼
                          AciPreviewProvisioner (AgentRuntime)
                                       │  ArmClient + DefaultAzureCredential
                                       ▼
                     ACI container group  (image: preview-runner)
                       public IP + random DnsNameLabel, port 8080
                       clones branch ▶ npm install ▶ npm run dev/start
                                       │
                  UI polls GET ──▶ Running + url ──▶ <iframe> / open / copy
                  UI heartbeat (60s) ──▶ extend expiresAt
                  PreviewIdleReaperService (60s) ──▶ tear down expired + orphans
```

### Where the code lives

- **Control plane in the API.** The user acts in the web app (talks to the API) and the
  idle-reaper is a long-running loop — both fit the always-on API container app, not Durable
  Functions. The API hosts `PreviewController`, `PreviewService`, and
  `PreviewIdleReaperService` (`BackgroundService`).
- **Provisioner shared in AgentRuntime.** The API references Core + AgentRuntime (not
  Workflows, which currently holds the only ACI SDK reference). Add
  `Azure.ResourceManager.ContainerInstance` to AgentRuntime and implement
  `IPreviewProvisioner` / `AciPreviewProvisioner` there.
- **Deliberate duplication:** the run path's `WorkspaceService` (Workflows) is **not**
  refactored onto the shared provisioner in v1 — isolation/safety. Folding it on later is a
  follow-up proposal, not part of this spec.

## Data model

New Cosmos container **`previewSessions`**, partition key **`/boardId`**.

```
PreviewSession
  id             string          // also the random DNS label, e.g. "tpv-7f3a9c2b"
  boardId        string          // partition key
  tenantId       string
  branch         string
  status         PreviewStatus   // Provisioning | Running | Failed | Stopped
  url            string?         // https://<id>.<region>.azurecontainer.io:8080
  containerName  string?         // ACI container-group name (== id)
  error          string?         // populated on Failed
  createdAt      DateTimeOffset
  lastActivityAt DateTimeOffset  // bumped by heartbeat
  expiresAt      DateTimeOffset  // lastActivityAt + idle window, capped at createdAt + max TTL
```

## Lifecycle

- **One active preview per board.** Starting a preview when one exists (any branch)
  **replaces** it: tear down the old container, create the new. Keeps cost bounded and state
  simple ("the current preview" for the board).
- **Start:** create `PreviewSession{status: Provisioning}`, persist, kick off ACI provision
  asynchronously. UI polls `GET` until `Running` or `Failed`.
- **Heartbeat:** while the Preview tab is open and the session is `Running`, the browser POSTs
  heartbeat every **60s**. Each sets `expiresAt = now + 15min` (idle window), capped at
  `createdAt + 45min` (hard max TTL).
- **Reaper** (`BackgroundService`, every **60s**): tears down any session with
  `expiresAt < now` → `Stopped`; also reconciles **orphans** — ACI groups tagged
  `tectika-preview=<boardId>` with no matching `Running`/`Provisioning` session are deleted
  (covers API restarts/crashes so paid containers never leak).
- **Manual stop:** `DELETE` tears down immediately → `Stopped`.

**Rationale for the numbers:** 15-min idle covers "stepped away" without paying for idle
containers; the 45-min hard cap is the backstop against a forgotten open tab heartbeating
forever. Shared-link viewers (no heartbeat of their own) rely on the owner's tab or the link
dying at idle — acceptable for v1.

## Preview runner image

`docker/preview-runner/` — `node:20-slim` base + `git`. Entrypoint:

1. Clone `REPO_URL`, checkout `GIT_BRANCH` (PAT via `.git-credentials`, same convention as the
   workspace entrypoint; PAT only set for private repos).
2. `cd` repo, detect package manager by lockfile (npm / pnpm / yarn), install.
3. `export PORT=8080 HOST=0.0.0.0 HOSTNAME=0.0.0.0`; run `npm run dev` if a `dev` script
   exists, else `npm start`.
4. On any failure (clone / install / missing start script / crash) exit non-zero → container
   terminates → API marks the session `Failed` with a reason.

### v1 framework/port limitation (accepted)

ACI fixes the exposed port at create time, so the runner forces the app onto
`PORT=8080`, `HOST=0.0.0.0`. This works for apps that honor `PORT`/`HOST` — **Next.js, CRA,
most Node servers** (the agent-built apps are Next-style). Frameworks that only accept a CLI
flag (e.g. raw Vite) won't bind correctly in v1. The later fix is **agent-declared start
command/port**. v1 ships the env convention + a clear `Failed` state.

## Provisioner (AgentRuntime)

`AciPreviewProvisioner : IPreviewProvisioner`, mirroring `WorkspaceService`'s ACI pattern:

- `ArmClient` + `DefaultAzureCredential`; container group with public IP + random
  `DnsNameLabel`, single TCP port 8080, user-assigned MI for ACR pull, region from config.
- Env: `REPO_URL`, `GIT_BRANCH`, `GIT_PAT` (SecureValue). Tag `tectika-preview=<boardId>` for
  orphan reconciliation.
- Methods: `ProvisionAsync(repo, branch, pat, dnsLabel, ct) → fqdn`,
  `DestroyAsync(name, ct)`, `ListPreviewGroupsAsync(ct)` (orphan reconciliation).
- PAT resolved via existing `ISecretProvider` from the board's `PatSecretName`.

## API surface

`PreviewController`, `[Authorize]`, tenant + board scoped (mirrors `RepoController.ResolveAsync`,
including the `409 GitHubNotConnected` guard):

- `POST /api/boards/{boardId}/preview` `{ branch }` → replace-and-start; returns
  `PreviewSession` (`Provisioning`).
- `GET /api/boards/{boardId}/preview` → current session, or `404` if none. (poll target)
- `POST /api/boards/{boardId}/preview/heartbeat` → bump `expiresAt`; `404` if none / not
  running.
- `DELETE /api/boards/{boardId}/preview` → stop now.

`PreviewService` (API) is the single writer of session state: writes the session, calls the
provisioner, transitions `Provisioning → Running` (on FQDN) or `→ Failed`.

## Security

- Unguessable URL via long random DNS label; gone at teardown.
- PAT passed as ACI `SecureValue` (never plain env/logs); public repos use none.
- All control endpoints tenant/board-authorized like `RepoController`.
- **Documented inherent risk:** the agent's app is publicly reachable while running, mitigated
  by the random URL + idle/cap teardown.

## Frontend

- Add `'preview'` to `RepoView`'s `Sub` union + a "Preview" sub-tab button; render
  `<PreviewTab boardId branch />` against the currently-selected branch.
- `PreviewTab` states: **Idle** (Start + target branch) → **Provisioning** (spinner) →
  **Running** (`<iframe>` of the URL + Open-in-new-tab + Copy-link + Stop) → **Failed**
  (reason + Retry).
- Polls `GET` every ~2s while `Provisioning`; heartbeats every 60s while `Running`; stops on
  unmount/Stop.
- `api.preview.{start,get,heartbeat,stop}` in `src/lib/api.ts`; `PreviewSession` + status type
  in `src/lib/types.ts`.

## Infra (idempotency rule)

- **Cosmos `previewSessions`** (`/boardId`): add to `CosmosDbService.ContainerDefinitions`
  **and** create manually via `az cosmosdb sql container create` on deploy (the
  `EnsureInfrastructureAsync`-swallows-failures caveat), or the feature 500s.
- **`preview-runner` image**: new Dockerfile + build/push step in `scripts/deploy.sh` (and
  `.ps1`); pushed to ACR like the other images.
- **API managed identity** needs **ACI create/delete on the resource group** (the Workflows MI
  has this; the API MI likely does not) → role assignment in `infra/` bicep (idempotent) +
  note the manual grant.
- **Config** for the API: `Preview:MiResourceId` (UAMI for ACR pull, same one workspaces use),
  `Preview:AcrImage` (preview-runner tag), `Preview:Region`, `Preview:ResourceGroup`, idle and
  cap minutes. Add to `infra/` app settings.

## Testing

- **Unit (xUnit, isolated behind `IPreviewProvisioner`):** session state transitions
  (Provisioning → Running / Failed / Stopped); expiry/heartbeat math (idle window + hard cap);
  DNS-label generation (valid + unguessable); branch → env mapping; reaper selection (expired +
  orphan). ACI and Cosmos mocked.
- **Frontend (`node --test`):** `api.preview.*` client shape + types; the PreviewTab
  state-machine reducer (pure) if extracted.
- No live-ACI integration test in v1 (real infra cost); the provisioner is a thin SDK adapter
  validated manually on deploy.

## Out of scope (v1) / follow-ups

- Agent-declared start command/port (removes the `PORT`/`HOST` limitation).
- Task-panel preview surface (v1 is board-level only).
- Concurrent previews / branch-vs-branch comparison.
- Build-to-static hosting path (CDN) for non-server apps.
- Refactoring the run-path `WorkspaceService` onto the shared provisioner.
- Auth-gated (proxied) previews.
```
