# TectikaAgents — Infrastructure

Idempotent, copy-to-any-tenant deployment. Re-running converges; it never errors
on "already exists" and never reverts CI-deployed container images.

> ⚠️ **Keep this folder current.** Any change that affects deployable resources,
> config, env vars, secrets, container apps, or CI **must** be reflected here in
> the same change. See the project rule in agent memory (`deployment-files-idempotency-rule`).

## What gets created

| Layer | Resource |
|---|---|
| Registry | Azure Container Registry (admin disabled; MI + AcrPull) |
| Observability | Log Analytics + Application Insights |
| Data | Cosmos DB (serverless, 5 containers), Service Bus (topic + sub + 2 queues), Storage, Key Vault |
| AI | **Azure AI Foundry** account (`AIServices`) + project + `gpt-4o` (GlobalStandard) — Basic Agent Service, keyless |
| Compute | Container Apps Env + **API** CA + **Web** CA; **Workflows** = Function App (Flex Consumption, dotnet-isolated .NET 9) |
| Identity | 3 user-assigned MIs (api, workflows, web) + all role assignments (keyless) |
| Directory | GitHub OIDC app, Web SPA app reg, API app reg (`entra.ps1`) |

All model/data access is **keyless** — managed identity + RBAC, no API keys or
connection strings in app settings.

## Files

| File | Purpose |
|---|---|
| `main.bicep` + `modules/*` | All ARM resources (subscription scope). Idempotent by construction. |
| `main.bicepparam` | Tenant-specific values. **Change `namePrefix` per tenant.** |
| `entra.ps1` | Microsoft Entra (directory) actions — isolated; needs directory permissions. |
| `deploy.ps1` | Orchestrator: deploy → entra → re-deploy → GitHub secrets/variables. |

## Prerequisites

- `az` CLI logged in, with rights to create resources **and assign roles** on the target subscription.
- `pwsh` (PowerShell 7+).
- *(optional)* `gh` CLI authenticated — to auto-set the GitHub Actions secrets/variables.
- A **Microsoft Entra / Directory admin** if the operator lacks permission to create app registrations (see below).

## Deploy from scratch

```bash
pwsh infra/deploy.ps1 \
  -SubscriptionId <sub-guid> \
  -GitHubOrg <org> \
  -GitHubRepo <repo> \
  -NamePrefix agentteam      # pick a unique prefix per tenant
```

What it does:
1. Resolves live container images (so a re-run never reverts CI-deployed code).
2. Deploys all Azure resources (Bicep) — **pass 1**.
3. Runs `entra.ps1` for the app registrations.
4. Re-deploys (**pass 2**) wiring the API's `AzureAd` config from the Entra ids.
5. Sets GitHub Actions secrets + variables (or prints them if `gh` is unavailable).

Then **push to `main`** (or run the workflows manually) — CI builds the images,
pushes to ACR, and deploys the API/Web Container Apps and the Workflows Function App.

### If you lack Entra (directory) permissions

Creating app registrations needs directory permissions separate from Azure RBAC.
If the operator lacks them, `entra.ps1` stops with a clear message and exit code 10;
`deploy.ps1` continues and finishes the Azure resources. A directory admin then runs:

```bash
pwsh infra/entra.ps1 -GitHubOrg <org> -GitHubRepo <repo> \
  -SubscriptionId <sub-guid> -ResourceGroup rg-<prefix>-dev-001 \
  -WebUrl https://<web-fqdn> -NamePrefix <prefix>
```

…and hands back the printed `apiClientId` / `platformClientId`. Re-run `deploy.ps1`
to wire them in and set the GitHub secrets.

## Idempotency notes

- ARM resources use PUT semantics — safe to re-run.
- Role assignments use deterministic GUID names → no duplicates.
- Entra actions look up existing objects by display name / credential name before creating.
- Container App images are passed in by `deploy.ps1` from the live app, so a redeploy
  keeps the CI-deployed image instead of reverting to the placeholder.

## Known TODOs (out of scope of the infra pass)

- `src/workflows/Dockerfile` is unused now that workflows deploy as a Function App
  (zip deploy). Left in place pending a decision to remove it.
- Full API bearer-token validation middleware wiring in application code.
- The larger plan's Agent Service tool-loop / MCP / ACA dynamic sessions.
