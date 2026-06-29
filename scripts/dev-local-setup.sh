#!/usr/bin/env bash
# =============================================================================
# dev-local-setup.sh
#
# One-time, idempotent setup for running the Tectika stack LOCALLY against the
# REAL Azure resources (real Cosmos data + real Foundry agents). It:
#
#   1. Verifies local tooling (dotnet / node / func / azurite / az).
#   2. Reads the live deployed configuration and writes the gitignored local
#      config files:
#         - src/api/TectikaAgents.Api/appsettings.Development.json
#         - src/workflows/local.settings.json
#         - src/web/tectika-board/.env.local
#   3. Ensures the signed-in user holds the data-plane RBAC roles that the
#      services' managed identities use (Service Bus + Key Vault). Cosmos and
#      Foundry roles are assumed already present; re-run reports if not.
#   4. Creates a dedicated Service Bus subscription so the local API receives
#      its own copy of agent events (no contention with the deployed API).
#
# Safe to re-run. See docs/local-dev.md for the full story.
#
# Usage:  scripts/dev-local-setup.sh [--no-grant]
#   --no-grant   Report missing RBAC roles + the exact az commands, but do not
#                create role assignments (use if you are not Owner/UAA).
#
# Overridable via env: TECTIKA_PREFIX (agentteam), TECTIKA_RG (rg-agentteam-dev-001),
#                      TECTIKA_LOCAL_SB_SUB (api-local), TECTIKA_API_PORT (5000)
# =============================================================================
set -euo pipefail

PREFIX="${TECTIKA_PREFIX:-agentteam}"
RG="${TECTIKA_RG:-rg-agentteam-dev-001}"
LOCAL_SUB="${TECTIKA_LOCAL_SB_SUB:-api-local}"
API_PORT="${TECTIKA_API_PORT:-5000}"
GRANT=1
[ "${1:-}" = "--no-grant" ] && GRANT=0

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_APP="ca-${PREFIX}-api"
FUNC_APP="func-${PREFIX}-workflows"

say()  { printf '\n\033[1m== %s\033[0m\n' "$1"; }
ok()   { printf '   [ok] %s\n' "$1"; }
warn() { printf '   [!!] %s\n' "$1"; }
die()  { printf '\n\033[31mERROR: %s\033[0m\n' "$1" >&2; exit 1; }

# ── 1. Tooling ───────────────────────────────────────────────────────────────
say "Checking local tooling"
for t in dotnet node npm func azurite az; do
  if command -v "$t" >/dev/null 2>&1; then ok "$t  ($($t --version 2>/dev/null | head -1))"; else die "missing required tool: $t"; fi
done
if dotnet --list-runtimes | grep -q 'Microsoft.NETCore.App 9\.'; then
  ok ".NET 9 runtime present (workflows target)"
else
  warn ".NET 9 runtime not installed — workflows (net9) will run on the .NET 10 runtime via DOTNET_ROLL_FORWARD=Major (dev-local.sh handles this)."
fi

az account show >/dev/null 2>&1 || die "not logged in to az — run 'az login' first"
SUB_NAME="$(az account show --query name -o tsv)"
ok "az logged in: $SUB_NAME"

# ── 2. Read live deployed config (source of truth for endpoints) ─────────────
say "Reading deployed configuration from $FUNC_APP"
get() { az functionapp config appsettings list -n "$FUNC_APP" -g "$RG" --query "[?name=='$1'].value | [0]" -o tsv 2>/dev/null; }
COSMOS_EP="$(get CosmosDb__AccountEndpoint)"
COSMOS_DB="$(get CosmosDb__DatabaseName)"
SB_FQDN="$(get ServiceBus__Namespace)"
FOUNDRY_EP="$(get Foundry__Endpoint)"
FOUNDRY_PROJ="$(get Foundry__ProjectName)"
FOUNDRY_PROJ_EP="$(get Foundry__ProjectEndpoint)"
FOUNDRY_MODEL="$(get Foundry__DefaultModel)"
KV_URI="$(get KeyVault__VaultUri)"
WS_RG="$(get Workspace__ResourceGroup)"
WS_IMAGE="$(get Workspace__Image)"
WS_MI="$(get Workspace__MiResourceId)"
WS_ACCOUNT="$(get AzureWebJobsStorage__accountName)"   # real Storage account for workspace snapshot blobs
[ -n "$COSMOS_EP" ] && [ -n "$FOUNDRY_PROJ_EP" ] && [ -n "$SB_FQDN" ] || die "could not read config from $FUNC_APP — check TECTIKA_PREFIX/TECTIKA_RG"
SB_NS="${SB_FQDN%%.*}"                                  # sb-agentteam.servicebus... -> sb-agentteam
KV_NAME="$(printf '%s' "$KV_URI" | sed -E 's#https://([^.]+)\..*#\1#')"
ok "cosmos=$COSMOS_EP db=$COSMOS_DB"
ok "servicebus=$SB_FQDN  keyvault=$KV_NAME"
ok "foundry project endpoint=$FOUNDRY_PROJ_EP  model=$FOUNDRY_MODEL"

# ── 3. Write local config files ──────────────────────────────────────────────
say "Writing local config files (gitignored)"

cat > "$ROOT/src/api/TectikaAgents.Api/appsettings.Development.json" <<JSON
{
  "MockDatabase": { "Enabled": false },
  "DevAuth": { "Enabled": true },
  "CosmosDb": {
    "AccountEndpoint": "$COSMOS_EP",
    "DatabaseName": "$COSMOS_DB",
    "SeedData": false
  },
  "Foundry": {
    "Endpoint": "$FOUNDRY_EP",
    "ProjectName": "$FOUNDRY_PROJ",
    "ProjectEndpoint": "$FOUNDRY_PROJ_EP",
    "DefaultModel": "$FOUNDRY_MODEL"
  },
  "ServiceBus": {
    "Namespace": "$SB_FQDN",
    "AgentEventsSubscription": "$LOCAL_SUB"
  },
  "KeyVault": { "VaultUri": "$KV_URI" },
  "WorkspaceSnapshots": { "AccountName": "$WS_ACCOUNT" },
  "DurableFunctions": { "StartUrl": "http://localhost:7071/api/pipelines/start" }
}
JSON
ok "src/api/TectikaAgents.Api/appsettings.Development.json"

cat > "$ROOT/src/workflows/local.settings.json" <<JSON
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "MockDatabase__Enabled": "false",
    "Foundry__UseMock": "false",
    "CosmosDb__AccountEndpoint": "$COSMOS_EP",
    "CosmosDb__DatabaseName": "$COSMOS_DB",
    "ServiceBus__Namespace": "$SB_FQDN",
    "ServiceBus__AgentEventsTopic": "agent-events",
    "ServiceBus__TaskTriggerQueue": "task-trigger",
    "ServiceBus__ApprovalsQueue": "approvals",
    "Foundry__Endpoint": "$FOUNDRY_EP",
    "Foundry__ProjectName": "$FOUNDRY_PROJ",
    "Foundry__ProjectEndpoint": "$FOUNDRY_PROJ_EP",
    "Foundry__DefaultModel": "$FOUNDRY_MODEL",
    "Foundry__IsOpenAiDirect": "false",
    "Foundry__ApiKey": "",
    "KeyVault__VaultUri": "$KV_URI",
    "WorkspaceSnapshots__AccountName": "$WS_ACCOUNT",
    "Api__BaseUrl": "http://localhost:$API_PORT",
    "Workspace__ResourceGroup": "$WS_RG",
    "Workspace__Image": "$WS_IMAGE",
    "Workspace__MiResourceId": "$WS_MI",
    "Logging__LogSensitiveContent": "true"
  }
}
JSON
ok "src/workflows/local.settings.json"

cat > "$ROOT/src/web/tectika-board/.env.local" <<ENV
# Local web -> local API. Regenerate with scripts/dev-local-setup.sh
NEXT_PUBLIC_API_URL=http://localhost:$API_PORT
ENV
ok "src/web/tectika-board/.env.local"

# ── 4. RBAC (data-plane roles for the signed-in user) ────────────────────────
say "Checking data-plane RBAC for signed-in user"
MYID="$(az ad signed-in-user show --query id -o tsv)"
SB_ID="$(az servicebus namespace show -n "$SB_NS" -g "$RG" --query id -o tsv)"
KV_ID="$(az keyvault show -n "$KV_NAME" -g "$RG" --query id -o tsv)"
SA_ID="$(az storage account show -n "$WS_ACCOUNT" -g "$RG" --query id -o tsv)"

ensure_role() {
  local role="$1" scope="$2"
  if [ -n "$(az role assignment list --assignee "$MYID" --role "$role" --scope "$scope" --query "[0].id" -o tsv 2>/dev/null)" ]; then
    ok "$role"
  elif [ "$GRANT" = "1" ]; then
    az role assignment create --assignee "$MYID" --role "$role" --scope "$scope" -o none && ok "granted: $role"
  else
    warn "MISSING: $role"
    printf '        az role assignment create --assignee %s --role "%s" --scope %s\n' "$MYID" "$role" "$scope"
  fi
}
ensure_role "Azure Service Bus Data Owner" "$SB_ID"
ensure_role "Key Vault Secrets User" "$KV_ID"
ensure_role "Storage Blob Data Contributor" "$SA_ID"   # workspace snapshot blobs (BlobWorkspaceSnapshotStore)
ok "Cosmos / Foundry data-plane roles are expected to be present already (managed via infra)."

# ── 5. Dedicated local Service Bus subscription (no event contention) ────────
say "Ensuring local Service Bus subscription '$LOCAL_SUB' on agent-events"
if az servicebus topic subscription show --namespace-name "$SB_NS" -g "$RG" --topic-name agent-events --name "$LOCAL_SUB" -o none 2>/dev/null; then
  ok "subscription '$LOCAL_SUB' already exists"
else
  az servicebus topic subscription create --namespace-name "$SB_NS" -g "$RG" --topic-name agent-events --name "$LOCAL_SUB" -o none
  ok "created subscription '$LOCAL_SUB'"
fi

# ── 6. Web deps ──────────────────────────────────────────────────────────────
if [ ! -d "$ROOT/src/web/tectika-board/node_modules" ]; then
  say "Installing web dependencies (npm install)"
  ( cd "$ROOT/src/web/tectika-board" && npm install )
fi

say "Setup complete"
printf '   Launch the stack with:  scripts/dev-local.sh up\n'
printf '   Docs:                   docs/local-dev.md\n\n'
