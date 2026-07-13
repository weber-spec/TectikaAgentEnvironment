<#
.SYNOPSIS
  dev-local-setup.ps1 - one-time, idempotent setup for running the Tectika stack
  LOCALLY against the REAL Azure resources (Windows PowerShell port of
  scripts/dev-local-setup.sh).

.DESCRIPTION
  1. Verifies local tooling (dotnet / node / npm / func / azurite / az).
  2. Reads the live deployed configuration and writes the gitignored local config:
       - src/api/TectikaAgents.Api/appsettings.Development.json
       - src/workflows/local.settings.json
       - src/web/tectika-board/.env.local
  3. Ensures the signed-in user holds the data-plane RBAC roles the services use
     (Service Bus + Key Vault + Storage Blob). Cosmos/Foundry roles are assumed present.
  4. Creates a dedicated Service Bus subscription so the local API gets its own copy
     of agent events (no contention with the deployed API).
  5. Runs `npm install` for the web app if node_modules is missing.

  Safe to re-run. See docs/local-dev.md for the full story.

.PARAMETER NoGrant
  Report missing RBAC roles + the exact commands, but do NOT create role assignments
  (use if you are not Owner/UAA, or prefer to grant them in the Azure portal).

.EXAMPLE
  .\scripts\dev-local-setup.ps1
.EXAMPLE
  .\scripts\dev-local-setup.ps1 -NoGrant

.NOTES
  Overridable via env: TECTIKA_PREFIX (agentteam), TECTIKA_RG (rg-agentteam-dev-001),
                       TECTIKA_LOCAL_SB_SUB (api-local), TECTIKA_API_PORT (5000)
#>
[CmdletBinding()]
param(
  [switch] $NoGrant
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --------------------------------------------------------------------------------------------------
# Configuration (defaults, each overridable via env var)
# --------------------------------------------------------------------------------------------------
$PREFIX    = if ($env:TECTIKA_PREFIX)       { $env:TECTIKA_PREFIX }       else { 'agentteam' }
$RG        = if ($env:TECTIKA_RG)           { $env:TECTIKA_RG }           else { 'rg-agentteam-dev-001' }
$LOCAL_SUB = if ($env:TECTIKA_LOCAL_SB_SUB) { $env:TECTIKA_LOCAL_SB_SUB } else { 'api-local' }
$API_PORT  = if ($env:TECTIKA_API_PORT)     { $env:TECTIKA_API_PORT }     else { '5138' }   # project convention (launchSettings + web .env.local)
$Grant     = -not $NoGrant

# The subscription/tenant that own $RG. Pinned rather than inherited from whatever `az account show`
# happens to return: on a machine signed in to several tenants, running setup against the wrong one
# silently generates config (and role grants) for the wrong resources. dev-local.ps1 pins the same pair.
$TENANT_ID = if ($env:TECTIKA_TENANT)       { $env:TECTIKA_TENANT }       else { '134f5740-154b-4915-9e20-7f25e65d6edc' }
$SUB_ID    = if ($env:TECTIKA_SUBSCRIPTION) { $env:TECTIKA_SUBSCRIPTION } else { '929e4f09-f929-4ebe-b146-3723b1e283b5' }

$ROOT     = Split-Path -Parent $PSScriptRoot
$FUNC_APP = "func-$PREFIX-workflows"

# --------------------------------------------------------------------------------------------------
# Pretty output + helpers
# --------------------------------------------------------------------------------------------------
function Say  ($m) { Write-Host "`n== $m" -ForegroundColor White }
function Ok   ($m) { Write-Host "   [ok] $m" }
function Warn ($m) { Write-Host "   [!!] $m" -ForegroundColor Yellow }
function Die  ($m) { Write-Host "`nERROR: $m" -ForegroundColor Red; exit 1 }

# Write text as UTF-8 without BOM (Windows PowerShell's Set-Content -Encoding UTF8 adds a BOM).
function Write-Utf8NoBom ($Path, $Content) {
  [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

# --------------------------------------------------------------------------------------------------
# 1. Tooling
# --------------------------------------------------------------------------------------------------
Say 'Checking local tooling'
foreach ($t in 'dotnet', 'node', 'npm', 'func', 'azurite', 'az') {
  $cmd = Get-Command $t -ErrorAction SilentlyContinue
  if ($cmd) {
    $v = (& $t --version 2>$null | Select-Object -First 1)
    Ok "$t  ($v)"
  }
  else { Die "missing required tool: $t" }
}
if ((dotnet --list-runtimes) -match 'Microsoft\.NETCore\.App 9\.') {
  Ok '.NET 9 runtime present (workflows target)'
}
else {
  Warn '.NET 9 runtime not installed - workflows (net9) will run on a newer runtime via DOTNET_ROLL_FORWARD=Major (dev-local.ps1 handles this).'
}

# az writes its diagnostics to stderr, which PowerShell 5.1 turns into a terminating
# NativeCommandError under $ErrorActionPreference='Stop' - the raw traceback would replace the
# guidance in Die below. Routing through cmd keeps stderr out of the pipeline.
function Invoke-Az  ([string] $Arguments) { & $env:ComSpec /c "az $Arguments >nul 2>&1"; return $LASTEXITCODE }
function Get-AzValue([string] $Arguments) { (& $env:ComSpec /c "az $Arguments 2>nul") | Select-Object -First 1 }

if ((Invoke-Az 'account show') -ne 0) { Die "not logged in to az - run 'az login --tenant $TENANT_ID' first" }

# Select the project subscription rather than trusting the active one, then prove the signed-in
# account can actually mint a token in its tenant. Without that proof setup happily generates config
# (and role grants) against the wrong resources, and the stack still starts: every AAD-backed
# dependency (Foundry, Key Vault, Service Bus) fails at runtime while Cosmos keeps working off its
# connection string - a silent, partial breakage that reads like a product bug.
if ((Get-AzValue 'account show --query id -o tsv') -ne $SUB_ID) {
  Warn "az is on '$(Get-AzValue 'account show --query name -o tsv')' - switching to the project subscription"
  if ((Invoke-Az "account set --subscription $SUB_ID") -ne 0) {
    Die "az has no access to subscription $SUB_ID - run 'az login --tenant $TENANT_ID'"
  }
}
if ((Invoke-Az "account get-access-token --tenant $TENANT_ID --scope https://ai.azure.com/.default") -ne 0) {
  Die "az is signed in as '$(Get-AzValue 'account show --query user.name -o tsv')', which cannot get a token in tenant $TENANT_ID - run 'az login --tenant $TENANT_ID'"
}

$SUB      = Get-AzValue 'account show --query id   -o tsv'
$SUB_NAME = Get-AzValue 'account show --query name -o tsv'
Ok "az logged in: $SUB_NAME"
Ok "subscription: $SUB (tenant $TENANT_ID)"

# --------------------------------------------------------------------------------------------------
# 2. Read live deployed config (source of truth for endpoints)
# --------------------------------------------------------------------------------------------------
Say "Reading deployed configuration from $FUNC_APP"
function Get-Setting ($name) {
  az functionapp config appsettings list -n $FUNC_APP -g $RG --subscription $SUB `
    --query "[?name=='$name'].value | [0]" -o tsv 2>$null
}
$COSMOS_EP       = Get-Setting 'CosmosDb__AccountEndpoint'
$COSMOS_DB       = Get-Setting 'CosmosDb__DatabaseName'
$SB_FQDN         = Get-Setting 'ServiceBus__Namespace'
$FOUNDRY_EP      = Get-Setting 'Foundry__Endpoint'
$FOUNDRY_PROJ    = Get-Setting 'Foundry__ProjectName'
$FOUNDRY_PROJ_EP = Get-Setting 'Foundry__ProjectEndpoint'
$FOUNDRY_MODEL   = Get-Setting 'Foundry__DefaultModel'
$KV_URI          = Get-Setting 'KeyVault__VaultUri'
$WS_RG           = Get-Setting 'Workspace__ResourceGroup'
$WS_IMAGE        = Get-Setting 'Workspace__Image'
$WS_MI           = Get-Setting 'Workspace__MiResourceId'
$WS_ACCOUNT      = Get-Setting 'AzureWebJobsStorage__accountName'   # real Storage account for workspace snapshot blobs

if (-not $COSMOS_EP -or -not $FOUNDRY_PROJ_EP -or -not $SB_FQDN) {
  Die "could not read config from $FUNC_APP - check TECTIKA_PREFIX/TECTIKA_RG"
}
$SB_NS   = $SB_FQDN.Split('.')[0]                 # sb-agentteam.servicebus... -> sb-agentteam
$KV_NAME = ([uri]$KV_URI).Host.Split('.')[0]      # https://kv-agentteam.vault... -> kv-agentteam
Ok "cosmos=$COSMOS_EP db=$COSMOS_DB"
Ok "servicebus=$SB_FQDN  keyvault=$KV_NAME"
Ok "foundry project endpoint=$FOUNDRY_PROJ_EP  model=$FOUNDRY_MODEL"

# --------------------------------------------------------------------------------------------------
# 3. Write local config files (gitignored)
# --------------------------------------------------------------------------------------------------
Say 'Writing local config files (gitignored)'

$apiSettings = @"
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
  "Workspace": {
    "ResourceGroup": "$WS_RG",
    "Image": "$WS_IMAGE",
    "MiResourceId": "$WS_MI"
  },
  "DurableFunctions": { "StartUrl": "http://localhost:7071/api/pipelines/start" }
}
"@
Write-Utf8NoBom (Join-Path $ROOT 'src\api\TectikaAgents.Api\appsettings.Development.json') $apiSettings
Ok 'src/api/TectikaAgents.Api/appsettings.Development.json'

$funcSettings = @"
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
"@
Write-Utf8NoBom (Join-Path $ROOT 'src\workflows\local.settings.json') $funcSettings
Ok 'src/workflows/local.settings.json'

$envLocal = @"
# Local web -> local API. Regenerate with scripts/dev-local-setup.ps1
NEXT_PUBLIC_API_URL=http://localhost:$API_PORT
"@
Write-Utf8NoBom (Join-Path $ROOT 'src\web\tectika-board\.env.local') $envLocal
Ok 'src/web/tectika-board/.env.local'

# --------------------------------------------------------------------------------------------------
# 4. RBAC (data-plane roles for the signed-in user)
# --------------------------------------------------------------------------------------------------
Say 'Checking data-plane RBAC for signed-in user'
$MYID = az ad signed-in-user show --query id -o tsv
$SB_ID = az servicebus namespace show -n $SB_NS    -g $RG --subscription $SUB --query id -o tsv
$KV_ID = az keyvault show          -n $KV_NAME     -g $RG --subscription $SUB --query id -o tsv
$SA_ID = az storage account show   -n $WS_ACCOUNT  -g $RG --subscription $SUB --query id -o tsv

function Ensure-Role ($role, $scope) {
  $existing = az role assignment list --assignee $MYID --role $role --scope $scope --subscription $SUB --query "[0].id" -o tsv 2>$null
  if ($existing) { Ok $role; return }

  $cmd = "az role assignment create --assignee $MYID --role `"$role`" --scope $scope --subscription $SUB"
  if ($Grant) {
    az role assignment create --assignee $MYID --role $role --scope $scope --subscription $SUB -o none 2>$null
    if ($LASTEXITCODE -eq 0) {
      Ok "granted: $role"
    }
    else {
      # Some az CLI builds fail role-assignment writes with MissingSubscription even when
      # the read path works - fall back to telling the user to grant it (portal or the cmd).
      Warn "could NOT auto-grant: $role  (grant it in the Azure portal -> resource -> Access control (IAM), or run:)"
      Write-Host "        $cmd"
    }
  }
  else {
    Warn "MISSING: $role"
    Write-Host "        $cmd"
  }
}
Ensure-Role 'Azure Service Bus Data Owner' $SB_ID
Ensure-Role 'Key Vault Secrets User'       $KV_ID
Ensure-Role 'Storage Blob Data Contributor' $SA_ID   # workspace snapshot blobs (BlobWorkspaceSnapshotStore)
Ok 'Cosmos / Foundry data-plane roles are expected to be present already (managed via infra).'

# --------------------------------------------------------------------------------------------------
# 5. Dedicated local Service Bus subscription (no event contention)
# --------------------------------------------------------------------------------------------------
Say "Ensuring local Service Bus subscription '$LOCAL_SUB' on agent-events"
az servicebus topic subscription show --namespace-name $SB_NS -g $RG --topic-name agent-events --name $LOCAL_SUB --subscription $SUB -o none 2>$null
if ($LASTEXITCODE -eq 0) {
  Ok "subscription '$LOCAL_SUB' already exists"
}
else {
  az servicebus topic subscription create --namespace-name $SB_NS -g $RG --topic-name agent-events --name $LOCAL_SUB --subscription $SUB -o none
  if ($LASTEXITCODE -eq 0) { Ok "created subscription '$LOCAL_SUB'" } else { Warn "could not create subscription '$LOCAL_SUB' - create it in the portal" }
}

# --------------------------------------------------------------------------------------------------
# 6. Web deps
# --------------------------------------------------------------------------------------------------
# Always run (idempotent + fast when up to date): a missing-only check leaves a stale
# node_modules out of sync when package.json gains a dependency (causes runtime 500s).
$webDir = Join-Path $ROOT 'src\web\tectika-board'
Say 'Syncing web dependencies (npm install)'
Push-Location $webDir
try { npm install } finally { Pop-Location }

Say 'Setup complete'
Write-Host '   Launch the stack with:  .\scripts\dev-local.ps1 up'
Write-Host '   Docs:                   docs/local-dev.md'
Write-Host ''
