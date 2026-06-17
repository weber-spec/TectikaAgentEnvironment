<#
.SYNOPSIS
  Deploy TectikaAgents surfaces (api / web / workflows) from Windows PowerShell.

.DESCRIPTION
  api / web   -> built in the cloud with `az acr build` (no local Docker) and rolled out
                 to their Azure Container Apps via `az containerapp update`.
  workflows   -> published to the Flex Consumption Function App with Azure Functions Core Tools.

  Images are tagged with the short git SHA (and :latest) so every rollout is traceable to a commit.
  Resource names are hardcoded to the live `agentteam` tenant but every one can be overridden with
  a TECTIKA_* environment variable (see the Configuration block) so the script is tenant-portable.

.EXAMPLE
  .\scripts\deploy.ps1 -Api
.EXAMPLE
  .\scripts\deploy.ps1 -Web -Workflows
.EXAMPLE
  .\scripts\deploy.ps1 -All
#>
[CmdletBinding()]
param(
  [switch] $Api,
  [switch] $Web,
  [switch] $Workflows,
  [switch] $All,
  [switch] $NoVerify,
  [switch] $AllowDirty,
  [switch] $Help
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Avoid the cp1252 UnicodeEncodeError that crashes az's log streaming on the Next.js banner glyph.
$env:PYTHONIOENCODING = 'utf-8'

# --------------------------------------------------------------------------------------------------
# Configuration (hardcoded defaults, each overridable via env var)
# --------------------------------------------------------------------------------------------------
function Get-Env([string]$name, [string]$default) {
  $v = [Environment]::GetEnvironmentVariable($name)
  if ([string]::IsNullOrEmpty($v)) { return $default } else { return $v }
}

$SubscriptionId  = Get-Env 'TECTIKA_SUBSCRIPTION_ID'  '929e4f09-f929-4ebe-b146-3723b1e283b5'
$ResourceGroup   = Get-Env 'TECTIKA_RESOURCE_GROUP'   'rg-agentteam-dev-001'
$AcrName         = Get-Env 'TECTIKA_ACR_NAME'         'tacragentteam'
$AcrLoginServer  = Get-Env 'TECTIKA_ACR_LOGIN_SERVER' "$AcrName.azurecr.io"
$AcaDomain       = Get-Env 'TECTIKA_ACA_DOMAIN'       'calmstone-c10c7a54.westeurope.azurecontainerapps.io'

$ApiApp          = Get-Env 'TECTIKA_API_APP'          'ca-agentteam-api'
$WebApp          = Get-Env 'TECTIKA_WEB_APP'          'ca-agentteam-web'
$FuncApp         = Get-Env 'TECTIKA_FUNC_APP'         'func-agentteam-workflows'

$ApiImage        = Get-Env 'TECTIKA_API_IMAGE'        'agentteam-api'
$WebImage        = Get-Env 'TECTIKA_WEB_IMAGE'        'agentteam-web'

$ApiFqdn         = "https://$ApiApp.$AcaDomain"
$WebFqdn         = "https://$WebApp.$AcaDomain"

# Health/smoke verification: how long to wait for a fresh revision to go Healthy/Running.
# ACA cold starts (image pull + app startup) can take a few minutes, so keep this generous.
$VerifyTimeoutSeconds = 300
$VerifyPollSeconds    = 5

# --------------------------------------------------------------------------------------------------
# Logging helpers (ASCII only)
# --------------------------------------------------------------------------------------------------
function Write-Log  { param($m) Write-Host "`n>> $m" -ForegroundColor Cyan }
function Write-Info { param($m) Write-Host "   $m" }
function Write-Warn { param($m) Write-Warning $m }
function Die        { param($m) Write-Host "`nERROR: $m" -ForegroundColor Red; exit 1 }

function Show-Usage {
@"
Deploy TectikaAgents surfaces to Azure.

Usage:
  .\scripts\deploy.ps1 [surfaces] [options]

Surfaces (pick at least one):
  -Api          Deploy the API container app ($ApiApp)
  -Web          Deploy the Web container app ($WebApp)
  -Workflows    Deploy the Functions app ($FuncApp)
  -All          Deploy all three (api, web, workflows)

Options:
  -NoVerify     Skip post-deploy health + smoke checks
  -AllowDirty   Allow deploying with an uncommitted working tree (tags are SHA-based)
  -Help         Show this help

Examples:
  .\scripts\deploy.ps1 -Api
  .\scripts\deploy.ps1 -Web -Workflows
  .\scripts\deploy.ps1 -All
"@ | Write-Host
}

# Run a native command and throw on non-zero exit (so $ErrorActionPreference='Stop' aborts).
function Invoke-Native {
  param([Parameter(Mandatory)][string]$Exe, [Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
  & $Exe @Arguments
  if ($LASTEXITCODE -ne 0) { Die "Command failed ($LASTEXITCODE): $Exe $($Arguments -join ' ')" }
}

# --------------------------------------------------------------------------------------------------
# Argument handling
# --------------------------------------------------------------------------------------------------
if ($Help) { Show-Usage; exit 0 }

if ($All) { $Api = $true; $Web = $true; $Workflows = $true }

if (-not ($Api -or $Web -or $Workflows)) {
  Show-Usage
  Die "No surface selected. Pass -Api, -Web, -Workflows, or -All."
}

$Verify = -not $NoVerify

# Resolve repo root relative to this script and operate from there.
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $RepoRoot

# --------------------------------------------------------------------------------------------------
# Prerequisite checks (fail fast, before any build)
# --------------------------------------------------------------------------------------------------
function Test-Prereqs {
  Write-Log "Checking prerequisites"

  if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Die "git not found on PATH." }
  if (-not (Get-Command az  -ErrorAction SilentlyContinue)) { Die "Azure CLI ('az') not found on PATH." }
  if ($Workflows -and -not (Get-Command func -ErrorAction SilentlyContinue)) {
    Die "Azure Functions Core Tools ('func') not found on PATH (needed for -Workflows)."
  }

  $currentSub = (& az account show --query id -o tsv 2>$null)
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($currentSub)) { Die "Not logged in to Azure. Run: az login" }

  if ($currentSub -ne $SubscriptionId) {
    Write-Warn "Active subscription is '$currentSub', expected '$SubscriptionId'."
    Write-Warn "Switch with: az account set --subscription $SubscriptionId"
    Die "Wrong subscription. Aborting to avoid deploying to the wrong tenant."
  }
  Write-Info "Azure subscription OK ($SubscriptionId)"
}

# --------------------------------------------------------------------------------------------------
# Git SHA / dirty-tree guard
# --------------------------------------------------------------------------------------------------
function Resolve-Sha {
  $script:Sha = (& git rev-parse --short HEAD).Trim()
  # Block on uncommitted changes to TRACKED files (staged or unstaged) so :$Sha is traceable.
  # Untracked files (e.g. .claude/, local notes) are not committed code, so they only warn.
  & git diff --quiet;        $unstaged = $LASTEXITCODE
  & git diff --cached --quiet; $staged = $LASTEXITCODE
  if ($unstaged -ne 0 -or $staged -ne 0) {
    if ($AllowDirty) {
      Write-Warn "Working tree has uncommitted changes; image tag :$($script:Sha) will NOT reflect them (-AllowDirty set)."
    } else {
      Die "Working tree has uncommitted changes to tracked files. Commit them (so :$($script:Sha) is traceable) or pass -AllowDirty."
    }
  }
  $untracked = (& git ls-files --others --exclude-standard)
  if (-not [string]::IsNullOrEmpty($untracked)) {
    Write-Warn "Untracked files present; they are not part of commit $($script:Sha). Proceeding."
  }
  Write-Log "Deploying from commit $($script:Sha)"
}

# --------------------------------------------------------------------------------------------------
# Verification helpers
# --------------------------------------------------------------------------------------------------
# Poll the revision running the image we just deployed (image tag == $Sha) until Healthy/Running,
# or time out. Targeting our own SHA avoids matching a draining old revision during the rollout.
function Wait-Revision {
  param([string]$App)
  $deadline = (Get-Date).AddSeconds($VerifyTimeoutSeconds)
  Write-Info "Waiting for the :$($script:Sha) revision of $App to go Healthy/Running (timeout ${VerifyTimeoutSeconds}s)..."
  while ((Get-Date) -lt $deadline) {
    $state = (& az containerapp revision list -n $App -g $ResourceGroup `
      --query "[?ends_with(properties.template.containers[0].image, ':$($script:Sha)')] | sort_by([],&properties.createdTime)[-1].[properties.healthState,properties.runningState]" `
      -o tsv 2>$null)
    if ($state) {
      $parts = $state -split "\s+"
      if ($parts.Count -ge 2 -and $parts[0] -eq 'Healthy' -and $parts[1] -eq 'Running') {
        Write-Info "Revision is Healthy/Running."
        return
      }
    }
    Start-Sleep -Seconds $VerifyPollSeconds
  }
  Die "$App :$($script:Sha) did not reach Healthy/Running within ${VerifyTimeoutSeconds}s. Check: az containerapp revision list -n $App -g $ResourceGroup"
}

function Test-Smoke {
  param([string]$Url)
  try {
    $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 30
    if ($resp.StatusCode -eq 200) { Write-Info "Smoke OK: $Url -> 200" }
    else { Die "Smoke FAILED: $Url -> $($resp.StatusCode)" }
  } catch {
    Die "Smoke FAILED: $Url -> $($_.Exception.Message)"
  }
}

# --------------------------------------------------------------------------------------------------
# Surface deployers
# --------------------------------------------------------------------------------------------------
function Deploy-Api {
  Write-Log "Deploying API -> $ApiApp"
  # Build context is the repo root; .dockerignore excludes src/web. The API image also pulls in
  # src/agentruntime (referenced by the Foundry runtime), so the root context is required.
  Invoke-Native az acr build -r $AcrName `
    -t "$($ApiImage):$($script:Sha)" -t "$($ApiImage):latest" `
    -f src/api/Dockerfile .
  Invoke-Native az containerapp update -n $ApiApp -g $ResourceGroup `
    --image "$AcrLoginServer/$($ApiImage):$($script:Sha)"
  Write-Info "API image $($ApiImage):$($script:Sha) rolled out."

  if ($Verify) {
    Wait-Revision $ApiApp
    Test-Smoke "$ApiFqdn/api/boards"
  }
}

function Deploy-Web {
  Write-Log "Deploying Web -> $WebApp"
  # Context (positional, last) is the Next.js app dir. NEXT_PUBLIC_API_URL is baked into the client
  # bundle at build time and must point at the live API.
  Invoke-Native az acr build -r $AcrName `
    -t "$($WebImage):$($script:Sha)" -t "$($WebImage):latest" `
    -f src/web/tectika-board/Dockerfile `
    --build-arg "NEXT_PUBLIC_API_URL=$ApiFqdn" `
    src/web/tectika-board/
  Invoke-Native az containerapp update -n $WebApp -g $ResourceGroup `
    --image "$AcrLoginServer/$($WebImage):$($script:Sha)"
  Write-Info "Web image $($WebImage):$($script:Sha) rolled out."

  if ($Verify) {
    Wait-Revision $WebApp
    Test-Smoke "$WebFqdn/boards"
  }
}

function Deploy-Workflows {
  Write-Log "Deploying Workflows -> $FuncApp"
  # Flex Consumption has no Kudu/SCM site, so zip-deploy fails. Core Tools is the supported path.
  # DOTNET_ROLL_FORWARD lets the host build the net9.0 project with a newer local SDK if needed.
  Push-Location src/workflows
  try {
    $env:DOTNET_ROLL_FORWARD = 'LatestMajor'
    Invoke-Native func azure functionapp publish $FuncApp --dotnet-isolated
  } finally {
    Pop-Location
  }
  Write-Info "Workflows published to $FuncApp."
}

# --------------------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------------------
Test-Prereqs
Resolve-Sha

# Deterministic order: api -> web -> workflows (web bundle targets the API FQDN).
if ($Api)       { Deploy-Api }
if ($Web)       { Deploy-Web }
if ($Workflows) { Deploy-Workflows }

Write-Log "Deploy complete (commit $($script:Sha))."
