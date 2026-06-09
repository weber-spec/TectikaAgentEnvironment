<#
.SYNOPSIS
    TectikaAgents — one-command, idempotent deployment to a fresh subscription.
.DESCRIPTION
    1. Resolves the live container images (so a re-run never reverts CI-deployed
       code back to the placeholder).
    2. Deploys ALL Azure resources via Bicep (main.bicep) — pass 1.
    3. Runs entra.ps1 for the directory app registrations (or prints a clear
       "directory admin required" message and continues).
    4. Re-deploys (pass 2, idempotent) with the Entra client ids so the API gets
       its AzureAd config.
    5. If `gh` is authenticated, pushes the GitHub Actions secrets + variables CI
       needs; otherwise prints them.
    Code itself is shipped by the GitHub Actions workflows on push to main.

    Re-runnable any number of times — every step converges.
.NOTES
    Prereq: az CLI logged in (Owner/Contributor + ability to assign roles).
            pwsh, and optionally gh (GitHub CLI) for secret automation.
#>
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$GitHubOrg,
    [Parameter(Mandatory)][string]$GitHubRepo,
    [string]$NamePrefix = 'agentteam',
    [string]$Location = 'westeurope',
    [string]$TenantId,
    [switch]$SkipEntra,
    [switch]$SkipGitHubSecrets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$RG          = "rg-$NamePrefix-dev-001"
$apiCaName   = "ca-$NamePrefix-api"
$webCaName   = "ca-$NamePrefix-web"
$placeholder = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

Write-Host "==> Setting subscription $SubscriptionId..."
az account set --subscription $SubscriptionId | Out-Null
if (-not $TenantId) { $TenantId = (az account show --query tenantId -o tsv).Trim() }

# ── Resolve live images (idempotency: don't clobber CI-deployed code) ─────────
function Get-LiveImage([string]$app) {
    $img = az containerapp show -n $app -g $RG --query 'properties.template.containers[0].image' -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and $img -and $img.Trim() -ne '') { return $img.Trim() }
    return $placeholder
}
$apiImage = Get-LiveImage $apiCaName
$webImage = Get-LiveImage $webCaName
Write-Host "    api image: $apiImage"
Write-Host "    web image: $webImage"

function Invoke-Deploy([string]$apiClientId, [string]$platformClientId) {
    # NOTE: az forbids combining a .bicepparam file with inline overrides, so the
    # orchestrator passes everything inline. Model settings use main.bicep defaults
    # (which match main.bicepparam). main.bicepparam is for manual `az deployment`.
    $raw = az deployment sub create `
        --name          'tectika-main' `
        --location      $Location `
        --template-file (Join-Path $here 'main.bicep') `
        --parameters    namePrefix=$NamePrefix location=$Location `
                        apiImage=$apiImage webImage=$webImage `
                        apiClientId=$apiClientId platformClientId=$platformClientId `
        -o json
    if ($LASTEXITCODE -ne 0) { throw 'Bicep deployment failed.' }
    return ($raw | ConvertFrom-Json).properties.outputs
}

# ── Pass 1: all ARM resources (Entra ids empty for now) ───────────────────────
Write-Host "`n==> Deploying infrastructure (pass 1)..."
$out = Invoke-Deploy '' ''
$webUrl         = $out.webUrl.value
$apiUrl         = $out.apiUrl.value
$acrLoginServer = $out.acrLoginServer.value
$functionApp    = $out.functionAppName.value
$apiCaName      = $out.apiContainerAppName.value
$webCaName      = $out.webContainerAppName.value
Write-Host "    web: $webUrl"
Write-Host "    api: $apiUrl"

# ── Entra (isolated; may require a directory admin) ───────────────────────────
$apiClientId = ''; $platformClientId = ''; $githubAppId = ''
if (-not $SkipEntra) {
    Write-Host "`n==> Running Entra app registrations..."
    $entraOut = Join-Path $here 'entra-outputs.json'
    & pwsh (Join-Path $here 'entra.ps1') `
        -GitHubOrg $GitHubOrg -GitHubRepo $GitHubRepo `
        -SubscriptionId $SubscriptionId -ResourceGroup $RG `
        -WebUrl $webUrl -NamePrefix $NamePrefix -OutFile $entraOut
    $entraExit = $LASTEXITCODE
    if ($entraExit -eq 10) {
        Write-Host "    Entra step skipped (insufficient directory permissions). Continuing." -ForegroundColor Yellow
    } elseif ($entraExit -ne 0) {
        throw "entra.ps1 failed with exit code $entraExit"
    } elseif (Test-Path $entraOut) {
        $e = Get-Content $entraOut -Raw | ConvertFrom-Json
        $apiClientId = $e.apiClientId; $platformClientId = $e.platformClientId; $githubAppId = $e.githubAppId
    }
} else { Write-Host "`n==> Skipping Entra (per -SkipEntra)." }

# ── Pass 2: re-deploy with Entra client ids (idempotent; only if we got them) ──
if ($apiClientId -and $platformClientId) {
    Write-Host "`n==> Deploying infrastructure (pass 2, wiring AzureAd)..."
    $out = Invoke-Deploy $apiClientId $platformClientId
}

# ── GitHub Actions secrets + variables ────────────────────────────────────────
$ghAvailable = $false
if (-not $SkipGitHubSecrets) {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        gh auth status 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ghAvailable = $true }
    }
}
if ($ghAvailable) {
    Write-Host "`n==> Setting GitHub Actions secrets + variables..."
    $repo = "$GitHubOrg/$GitHubRepo"
    function Set-GhSecret($n,$v)   { if ($v) { gh secret   set $n   -R $repo --body "$v" | Out-Null; Write-Host "    secret   $n" } }
    function Set-GhVariable($n,$v) { if ($v) { gh variable set $n   -R $repo --body "$v" | Out-Null; Write-Host "    variable $n" } }
    Set-GhSecret   'AZURE_CLIENT_ID'       $githubAppId
    Set-GhSecret   'AZURE_TENANT_ID'       $TenantId
    Set-GhSecret   'AZURE_SUBSCRIPTION_ID' $SubscriptionId
    Set-GhSecret   'ACR_LOGIN_SERVER'      $acrLoginServer
    Set-GhSecret   'NEXT_PUBLIC_API_URL'   $apiUrl
    Set-GhSecret   'NEXT_PUBLIC_CLIENT_ID' $platformClientId
    Set-GhVariable 'RESOURCE_GROUP'        $RG
    Set-GhVariable 'API_CONTAINER_APP'     $apiCaName
    Set-GhVariable 'WEB_CONTAINER_APP'     $webCaName
    Set-GhVariable 'FUNCTION_APP_NAME'     $functionApp
} else {
    Write-Host "`n==> gh not authenticated — set these GitHub Actions values manually:" -ForegroundColor Yellow
    Write-Host "    secret   AZURE_CLIENT_ID       = $githubAppId"
    Write-Host "    secret   AZURE_TENANT_ID       = $TenantId"
    Write-Host "    secret   AZURE_SUBSCRIPTION_ID = $SubscriptionId"
    Write-Host "    secret   ACR_LOGIN_SERVER      = $acrLoginServer"
    Write-Host "    secret   NEXT_PUBLIC_API_URL   = $apiUrl"
    Write-Host "    secret   NEXT_PUBLIC_CLIENT_ID = $platformClientId"
    Write-Host "    variable RESOURCE_GROUP        = $RG"
    Write-Host "    variable API_CONTAINER_APP     = $apiCaName"
    Write-Host "    variable WEB_CONTAINER_APP     = $webCaName"
    Write-Host "    variable FUNCTION_APP_NAME     = $functionApp"
}

Write-Host "`n============================================================"
Write-Host " DEPLOYMENT COMPLETE"
Write-Host "   API : $apiUrl"
Write-Host "   Web : $webUrl"
Write-Host "   Fn  : $functionApp"
Write-Host ""
Write-Host " Next: push to 'main' (or run the workflows) to build + deploy code."
Write-Host "============================================================"
