# Deploy scripts

Cross-platform helpers to deploy the three TectikaAgents surfaces to Azure. Run whichever
matches your shell — they share the same interface and run the same `az` / `func` commands:

| Shell                | Script                  |
| -------------------- | ----------------------- |
| WSL / Linux (bash)   | `scripts/deploy.sh`     |
| Windows (PowerShell) | `scripts/deploy.ps1`    |

## What it does

- **api** / **web** — builds the image in the cloud with `az acr build` (no local Docker), tags it
  with the short git SHA **and** `:latest`, then rolls it out to the Azure Container App with
  `az containerapp update`.
- **workflows** — publishes the Flex Consumption Function App with Azure Functions Core Tools
  (`func azure functionapp publish`). Zip-deploy does not work on Flex Consumption.

Every image tag is the short git SHA, so each rollout is traceable to a commit. The script aborts
if the working tree is dirty (override with `--allow-dirty` / `-AllowDirty`).

## Usage

```bash
# bash (WSL / Linux)
scripts/deploy.sh --api
scripts/deploy.sh --web --workflows
scripts/deploy.sh --all
scripts/deploy.sh --api --no-verify      # skip health + smoke checks
```

```powershell
# PowerShell (Windows)
.\scripts\deploy.ps1 -Api
.\scripts\deploy.ps1 -Web -Workflows
.\scripts\deploy.ps1 -All
.\scripts\deploy.ps1 -Api -NoVerify
```

| Flag (bash / PowerShell)      | Effect                                                          |
| ----------------------------- | -------------------------------------------------------------- |
| `--api`     / `-Api`          | Deploy the API container app                                   |
| `--web`     / `-Web`          | Deploy the Web container app                                   |
| `--workflows` / `-Workflows`  | Deploy the Functions app                                       |
| `--all`     / `-All`          | Deploy all three                                               |
| `--no-verify` / `-NoVerify`   | Skip the post-deploy health + smoke checks                     |
| `--allow-dirty` / `-AllowDirty` | Allow deploying with an uncommitted working tree             |

## Verification

Unless `--no-verify` is set, after each container-app rollout the script polls the new revision
until it reports `Healthy/Running` (3-minute timeout) and then smoke-tests the public endpoint
(api `/api/boards` → 200, web `/boards` → 200). For workflows, a zero exit from `func` (which
prints `Host ... Running` and the function list on success) is the verification signal.

## Prerequisites

- `az` logged in (`az login`) and pointed at the expected subscription — the script aborts on a
  mismatch to avoid deploying to the wrong tenant.
- `func` (Azure Functions Core Tools 4.x) on PATH — only required for `--workflows`.
- On WSL, use the **Linux** `az` (not the Windows `az.exe`), which builds and streams cleanly.

## Configuration

Resource names default to the live `agentteam` tenant. Every one can be overridden with an
environment variable for portability to another tenant — no script edits needed:

| Env var                      | Default                                                       |
| ---------------------------- | ------------------------------------------------------------- |
| `TECTIKA_SUBSCRIPTION_ID`    | `929e4f09-f929-4ebe-b146-3723b1e283b5`                        |
| `TECTIKA_RESOURCE_GROUP`     | `rg-agentteam-dev-001`                                        |
| `TECTIKA_ACR_NAME`           | `tacragentteam`                                               |
| `TECTIKA_ACR_LOGIN_SERVER`   | `<acr-name>.azurecr.io`                                       |
| `TECTIKA_ACA_DOMAIN`         | `calmstone-c10c7a54.westeurope.azurecontainerapps.io`         |
| `TECTIKA_API_APP`            | `ca-agentteam-api`                                            |
| `TECTIKA_WEB_APP`            | `ca-agentteam-web`                                            |
| `TECTIKA_FUNC_APP`           | `func-agentteam-workflows`                                    |
| `TECTIKA_API_IMAGE`          | `agentteam-api`                                               |
| `TECTIKA_WEB_IMAGE`          | `agentteam-web`                                               |

## Scope

These scripts ship **images** and roll them out. They do not manage infra config — creating Cosmos
containers, setting app settings/env vars, or Entra/secrets are separate concerns (see `infra/`).
