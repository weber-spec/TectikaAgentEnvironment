# Local development — run the stack without deploying

Run the four moving parts of Tectika **locally** while talking to the **real
Azure resources** (real Cosmos data, real Foundry agents, real Service Bus).
No `az containerapp update`, no `func publish`, no waiting on 4 deploys — edit,
save, and the change is live in seconds.

This works because every Azure call in the codebase is **keyless** — it uses
`DefaultAzureCredential`, which resolves to your `az login` identity locally.
Point config at the real endpoints, hold the right RBAC roles, and a process on
your machine reaches the same data the deployed services do.

---

## What runs where

| Process | Command | URL | Notes |
|---|---|---|---|
| **Web** (Next.js) | `npm run dev` | http://localhost:3000 | hot reload |
| **API** (.NET 10) | `dotnet watch run` | http://localhost:5000 | hot reload; real Cosmos/Foundry/Service Bus |
| **Workflows** (Durable Functions, net9) | `func start` | http://localhost:7071 | runs the agent orchestrations |
| **Azurite** | `azurite` | :10000-10002 | local Storage for Durable state + workspace blobs |
| **Agent workspace** | — | (stays in Azure) | provisioned on demand as a real ACI |

The agent workspace/sandbox is **not** run locally — when an agent needs it,
the local Workflows process provisions a real ACI in Azure (an outbound ARM
call) and talks to it over HTTP, exactly as in production.

---

## One-time setup

You need: `az` (logged in), and `dotnet`, `node`/`npm`, `func` (Azure Functions
Core Tools), `azurite`. RBAC role assignment requires **Owner** or **User Access
Administrator** on the resource group (use `--no-grant` if you are neither — the
script then prints the exact commands for an admin to run).

```bash
scripts/dev-local-setup.sh
```

This is idempotent and safe to re-run. It:

1. Verifies tooling and the .NET runtime.
2. Reads the **live deployed config** and writes the gitignored local config
   files (so they always match what production uses):
   - `src/api/TectikaAgents.Api/appsettings.Development.json`
   - `src/workflows/local.settings.json`
   - `src/web/tectika-board/.env.local`
3. Ensures your identity holds the data-plane roles the managed identities use
   (Service Bus Data Owner, Key Vault Secrets User; Cosmos + Foundry you already
   have via infra).
4. Creates a dedicated **`api-local`** Service Bus subscription on the
   `agent-events` topic (see *Service Bus* below).

> The config files are **gitignored** — they live in your working tree and
> survive branch switches. Re-run the setup script after the infra changes
> (new endpoints, model swap, etc.) to refresh them.

## Run it

```bash
scripts/dev-local.sh up       # start web + api + workflows + azurite
scripts/dev-local.sh logs     # tail -F all logs
scripts/dev-local.sh down     # stop everything
scripts/dev-local.sh status
```

Open http://localhost:3000. The first .NET build takes ~30–60s.

For active development many people prefer **one terminal per service** (clearer
logs, restart one without the others):

```bash
# API  (bind 0.0.0.0, not localhost — see the WSL note in "gotchas")
cd src/api/TectikaAgents.Api
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:5000 dotnet watch run --no-launch-profile

# Workflows  (DOTNET_ROLL_FORWARD: see "net9" note below)
cd src/workflows
azurite --silent --location /tmp/azurite &      # if not already running
DOTNET_ROLL_FORWARD=Major func start --port 7071

# Web
cd src/web/tectika-board
npm run dev
```

---

## The three modes

| Mode | How | Use for |
|---|---|---|
| **Full local + real Azure** (this doc) | configs point at real endpoints, `DevAuth` on | testing real data + agents, end-to-end runs |
| **Mock** (no Azure) | `"MockDatabase": { "Enabled": true }` in `appsettings.json` | pure UI work, offline, fast |
| **Selective** | run one service locally, leave the rest deployed | iterating on a single service |

`DevAuth:Enabled=true` (the default) bypasses Entra with an anonymous user —
you get real **data** without wiring browser login. The frontend doesn't attach
auth tokens yet, so nothing else is needed locally.

---

## Things to know (the gotchas)

**Service Bus — no contention.** The API listens to the `agent-events` topic for
live (SSE) updates. The deployed API uses subscription `api-sub`; setup gives
your local API its own `api-local` subscription. Topic subscriptions each get a
**full copy** of every event, so local and deployed run side by side — your
local UI gets live agent updates and you don't have to pause anything.

**Durable Functions run on Azurite.** The orchestration **task hub** uses the
local Azurite emulator, not the real `stagentteamflows` account — so your local
runs never collide with the deployed function app's task hub. (Workspace
**snapshot** blobs are the exception: they use the *real* Storage account, because
`BlobWorkspaceSnapshotStore` is identity-based and Azurite can't serve it — hence
the Storage Blob role below.) Your **business** data (tasks, runs, boards) lives
in the real Cosmos DB. A run started in the cloud can't be *resumed* locally (its
durable state is in Azure), but you can start fresh runs locally against real data all day.

**WSL2 networking — bind `0.0.0.0`.** On WSL2 the browser runs on Windows; a
service bound to `localhost`/loopback *inside* WSL isn't reachable from it (you'd
get `ERR_CONNECTION_REFUSED`), so the API binds `0.0.0.0`, like the web and func
already do. If you still can't reach the API, your WSL may not forward Windows
localhost — point `NEXT_PUBLIC_API_URL` at the WSL IP (`hostname -I`) instead of
`localhost`.

**You share the real dev data.** Local writes go to the same Cosmos DB the
deployed dev environment uses. That's the point ("use the real data"), but be
aware your experiments mutate shared dev state.

**net9 workflows on the net10 runtime.** The Workflows project targets .NET 9;
if only the .NET 10 runtime is installed, `func start` runs the worker via
`DOTNET_ROLL_FORWARD=Major` (the launcher sets this). For exact parity you can
install the .NET 9 runtime instead. The API targets .NET 10 (no roll-forward).

**Telemetry is off locally.** `APPLICATIONINSIGHTS_CONNECTION_STRING` is left
unset, so local runs don't publish traces into the shared App Insights. Set it
in the config if you want local traces there.

**WSL inotify limit.** File-watchers (VS Code + `dotnet watch` + next) share a
small per-user inotify-instance budget (`fs.inotify.max_user_instances`, often
128). Two safeguards are built in: `up` is **idempotent** (it stops the previous
instances and frees the ports first, so watchers never pile up), and the API's
`dotnet watch` uses **polling** (`DOTNET_USE_POLLING_FILE_WATCHER=true`, no
inotify). If you still see `inotify instances ... reached` (e.g. VS Code alone is
near the cap), raise it:
`echo fs.inotify.max_user_instances=512 | sudo tee /etc/sysctl.d/99-inotify.conf && sudo sysctl --system`.
Symptom when exhausted: `dotnet watch` dies → API down → `ERR_CONNECTION_REFUSED`.

**Agent workspace/sandbox.** Provisioning a workspace ACI from a local Workflows
process is the least-exercised path. It should work (ARM call with your
identity + the `mi-agentteam-workflows` identity), but if a sandbox tool misbehaves
locally, that's the first place to look.

---

## RBAC reference

Roles your `az login` identity needs (the setup script checks/grants these):

| Resource | Role | Why |
|---|---|---|
| Cosmos DB account | Cosmos DB Built-in Data Contributor | read/write tasks, runs, boards |
| Foundry project | Azure AI User (Foundry User) | create/run agents |
| Service Bus namespace | Azure Service Bus Data Owner | publish + consume agent events |
| Key Vault | Key Vault Secrets User | GitHub-tool secret (lazy; only for GitHub actions) |
| Storage account (`stagentteamflows`) | Storage Blob Data Contributor | workspace snapshot blobs (`BlobWorkspaceSnapshotStore`) |

The Durable Functions **task hub** uses Azurite locally (no Storage role needed
for that) — but the workspace **snapshot** store talks to the real Storage
account via your identity, hence the Blob role above.

---

## Cleanup

```bash
scripts/dev-local.sh down
rm -rf .dev-local            # logs, pids, azurite data
```

To remove the local Service Bus subscription:

```bash
az servicebus topic subscription delete \
  --namespace-name sb-agentteam -g rg-agentteam-dev-001 \
  --topic-name agent-events --name api-local
```
