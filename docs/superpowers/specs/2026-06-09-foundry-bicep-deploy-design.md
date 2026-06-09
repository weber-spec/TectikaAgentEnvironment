# Idempotent Bicep Deployment + Azure AI Foundry (Agent Service) — Design

> Status: **approved** (2026-06-09). Branch: `feat/foundry-bicep-deploy`.

## Goal & guiding rule

This repository is a **template meant to be copied to any Azure tenant**. The `infra/`
folder must, against a fresh subscription, provision the **full** project on a new
resource group with **complete idempotency** (re-running converges, never errors on
"already exists", never reverts CI-deployed code to placeholder images). See memory
rule `deployment-files-idempotency-rule`.

This session: add the **Azure AI Foundry (Agent Service)** resources + configuration,
and **harden the whole deploy** so it is correct, idempotent, and easy to stand up in
a new tenant. Application **code** is deployed by the existing GitHub Actions CI on
push to `main`; the bootstrap sets the GitHub secrets so CI works on first push.

## Locked decisions

| Dimension | Decision |
|---|---|
| Runtime resource | **AI Foundry account + project (Agent Service)** — `Microsoft.CognitiveServices/accounts` kind=`AIServices` + child project. |
| Model deployment | Single `gpt-4o`, **GlobalStandard**, parameterized version+capacity (template can add more). |
| Agent Service setup | **Basic** (Microsoft-managed thread/file state — no BYO Cosmos/Search/Storage, no capability host). |
| Auth to model | **Keyless** — attached user-assigned MI + `DefaultAzureCredential`; no API key. |
| Tooling | **Approach A** — declarative **Bicep** for all ARM resources + a thin **bootstrap script** for the non-ARM Entra bits. |
| Code deploy | **CI on push to `main`** (existing workflows); bootstrap sets GitHub secrets. |
| Workflows hosting | Dedicated **Azure Function App** (`func-agentteam-workflows`), Linux, dotnet-isolated .NET 9, **Flex Consumption**. |
| Entra actions | Isolated in their own file (`entra.ps1`); deploy tries it and, on a directory-permission failure, prints a clear "an authorized directory admin must run this" message and continues. |
| Region | `westeurope` (parameterized). |
| Names | Existing names kept as defaults, parameterized via `namePrefix`/`location`. |

## File layout (`infra/`)

```
infra/
  main.bicep                 # subscription scope: RG + everything in it
  modules/
    registry.bicep           # ACR (admin disabled)
    observability.bicep      # Log Analytics + Application Insights
    data.bicep               # Cosmos (db + 5 containers), Service Bus (topic/sub/2 queues), Storage, Key Vault
    identities.bicep         # 3 user-assigned MIs: api, workflows, web
    foundry.bicep            # AIServices account + project + gpt-4o deployment
    functionapp.bicep        # Function App (workflows) + Flex Consumption plan
    containerapps.bicep      # Container Apps Env + API CA + Web CA
    rbac.bicep               # ALL role assignments (deterministic GUIDs)
  main.bicepparam            # tenant-specific values
  entra.ps1                  # all directory actions, guarded + clear auth-failure message
  deploy.ps1                 # orchestrator: deploy -> entra -> deploy(pass2) -> gh secrets
  README.md                  # from-scratch runbook
```

## Foundry resources (`foundry.bicep`)

- `Microsoft.CognitiveServices/accounts` kind=`AIServices`, SKU `S0`, system-assigned
  identity, `customSubDomainName` set (required for AAD/keyless), `allowProjectManagement: true`.
- Child **project** (`accounts/projects`).
- Child **deployment `gpt-4o`**, SKU `GlobalStandard`, `model.format=OpenAI`,
  parameterized `version` + `capacity`.
- **Basic** Agent Service — no capability host, no BYO stores.

Config wired into API + Function App env (keyless):
`Foundry__Endpoint` (account endpoint), `Foundry__ProjectName`,
`Foundry__DefaultModel=gpt-4o`, `Foundry__IsOpenAiDirect=false`, `Foundry__ApiKey` empty
→ code's else-branch uses `DefaultAzureCredential` with the attached MI
(`src/workflows/Services/WorkflowAgentRunner.cs`).

## RBAC (`rbac.bicep`) — all keyless, deterministic GUIDs

- **api MI** & **workflows MI**: Cosmos DB Built-in Data Contributor (data plane),
  Azure Service Bus Data Owner, Key Vault Secrets User, AcrPull,
  **Cognitive Services OpenAI User** (Foundry account) + **Azure AI User** (project, Agent Service data plane).
- **web MI** (new): **AcrPull only** (least privilege).

Role assignment names use `guid(scope, principalId, roleId)` so re-runs never duplicate.

## Correctness-bug fixes (full hardening)

1. **Workflows = real Function App** (`func-agentteam-workflows`), attached to the
   workflows MI, reusing the `stagentteamflows` storage for `AzureWebJobsStorage`. This
   is what `deploy-workflows.yml` already targets, so CI starts working. The workflows
   `Dockerfile` becomes unused — flagged as a TODO, not deleted.
2. **Web Container App** gets its own MI + `registryIdentity` + AcrPull → image pulls succeed.
3. **No image reset on re-run** — container `image` is managed so a redeploy does not
   clobber the CI-deployed image (use `existing` image value / `latest` tag pattern).

## Entra isolation + failure handling (`entra.ps1`)

Each action guarded by an existence check (idempotent):
- **GitHub OIDC**: app reg + SP + federated credential (`repo:<org>/<repo>:ref:refs/heads/main`).
- **Web SPA** app reg: SPA redirect URI = web FQDN; outputs `NEXT_PUBLIC_CLIENT_ID`.
- **API** app reg: exposes an API scope/audience; outputs client id.

On a Microsoft Graph `Authorization_RequestDenied` / 403, stop with:
> ❌ You lack Microsoft Entra (directory) permissions to create app registrations.
> Azure resources were still deployed. Ask a Directory admin to run `infra/entra.ps1`,
> then re-run `deploy.ps1` (or supply the printed client IDs as parameters).

## Orchestration (`deploy.ps1`) — breaks the Entra↔ContainerApp dependency cycle

1. `az deployment sub create` (pass 1): all ARM + Foundry; container apps on placeholder
   image; outputs API/Web FQDNs.
2. Run `entra.ps1` with those FQDNs → app regs + federated cred → client IDs (or the
   clear auth-failure message, then continue).
3. `az deployment sub create` (pass 2, idempotent): pass Entra client IDs as params so
   API env gets `AzureAd__ClientId`/audience. Converges, no churn.
4. If `gh` is authenticated: `gh secret set` for `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, `ACR_LOGIN_SERVER`, `NEXT_PUBLIC_API_URL`,
   `NEXT_PUBLIC_CLIENT_ID`. Else print them.
5. Print runbook: push to `main` → CI builds + deploys code.

**Prerequisites:** `az` CLI logged in; `gh` (optional, for secret automation); a
directory admin for step 2 if the operator lacks Entra permissions.

## Out of scope this session (flagged, not built)

- The larger plan's Agent Service tool-loop / MCP / ACA dynamic sessions.
- Full API bearer-validation middleware wiring in application code.
- The now-unused workflows `Dockerfile` (left in place with a TODO).
