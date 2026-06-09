<#
.SYNOPSIS
    TectikaAgents - Microsoft Entra (directory) actions, ISOLATED from ARM deploy.
.DESCRIPTION
    Creates the three app registrations the project needs:
      1. GitHub Actions OIDC app  (+ SP + federated credential)  -> CI auth
      2. Web/platform SPA app reg                                -> NEXT_PUBLIC_CLIENT_ID
      3. API app reg (exposes api://<id>/access_as_user)         -> AzureAd__ClientId

    Every action is idempotent (looked up by displayName / name before create).

    These operations require Microsoft Entra DIRECTORY permissions, which are a
    SEPARATE grant from Azure RBAC. If the running user lacks them, the script
    stops with a clear message and exit code 10 - a directory admin should then
    run THIS file and hand back the printed client ids.

    Writes results as JSON to -OutFile and prints them.
.NOTES
    Prereq: az CLI logged in. Run standalone or via deploy.ps1.
#>
param(
    [Parameter(Mandatory)][string]$GitHubOrg,
    [Parameter(Mandatory)][string]$GitHubRepo,
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$WebUrl,
    [string]$NamePrefix = 'agentteam',
    [string]$OutFile = 'entra-outputs.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -- az wrapper: surfaces directory-permission failures clearly ----------------
function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$Args, [switch]$AllowFail)
    $out = & az @Args 2>&1
    if ($LASTEXITCODE -ne 0) {
        $text = ($out | Out-String)
        if ($text -match 'Authorization_RequestDenied|Insufficient privileges|does not have permission|Forbidden|\b403\b') {
            Write-Host ""
            Write-Host "============================================================" -ForegroundColor Red
            Write-Host " NOT AUTHORIZED FOR MICROSOFT ENTRA (DIRECTORY) ACTIONS" -ForegroundColor Red
            Write-Host "============================================================" -ForegroundColor Red
            Write-Host " Azure resources may already be deployed, but creating app" -ForegroundColor Yellow
            Write-Host " registrations requires directory permissions you don't have." -ForegroundColor Yellow
            Write-Host ""
            Write-Host " Ask a Microsoft Entra / Directory admin to run:" -ForegroundColor Yellow
            Write-Host "   pwsh infra/entra.ps1 -GitHubOrg $GitHubOrg -GitHubRepo $GitHubRepo \`" -ForegroundColor Cyan
            Write-Host "       -SubscriptionId $SubscriptionId -ResourceGroup $ResourceGroup \`" -ForegroundColor Cyan
            Write-Host "       -WebUrl $WebUrl -NamePrefix $NamePrefix" -ForegroundColor Cyan
            Write-Host ""
            Write-Host " Then re-run deploy.ps1 (it will pick up the app ids), or pass" -ForegroundColor Yellow
            Write-Host " the printed client ids to the bicep deployment manually." -ForegroundColor Yellow
            Write-Host "============================================================" -ForegroundColor Red
            exit 10
        }
        if ($AllowFail) { return $null }
        throw "az $($Args -join ' ') failed:`n$text"
    }
    return $out
}

function Get-AppId([string]$displayName) {
    $id = (Invoke-Az @('ad','app','list','--display-name',$displayName,'--query','[0].appId','-o','tsv'))
    return ($id | Out-String).Trim()
}

Write-Host "==> Entra: ensuring app registrations (idempotent)..."

# -- 1. GitHub Actions OIDC app ------------------------------------------------
$ghName = "sp-$NamePrefix-github"
$ghAppId = Get-AppId $ghName
if (-not $ghAppId) {
    Write-Host "    creating $ghName"
    $ghAppId = (Invoke-Az @('ad','app','create','--display-name',$ghName,'--query','appId','-o','tsv') | Out-String).Trim()
} else { Write-Host "    reusing $ghName ($ghAppId)" }

# Service principal (idempotent)
$ghSpOid = (Invoke-Az @('ad','sp','show','--id',$ghAppId,'--query','id','-o','tsv') -AllowFail | Out-String).Trim()
if (-not $ghSpOid) {
    $ghSpOid = (Invoke-Az @('ad','sp','create','--id',$ghAppId,'--query','id','-o','tsv') | Out-String).Trim()
}

# RBAC for CI: Contributor on the RG + AcrPush handled at RG scope (ACR is in RG)
$rgScope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
Invoke-Az @('role','assignment','create','--assignee-object-id',$ghSpOid,'--assignee-principal-type','ServicePrincipal','--role','Contributor','--scope',$rgScope) -AllowFail | Out-Null
Invoke-Az @('role','assignment','create','--assignee-object-id',$ghSpOid,'--assignee-principal-type','ServicePrincipal','--role','AcrPush','--scope',$rgScope) -AllowFail | Out-Null

# Federated credential for main branch (idempotent by name)
$fedName = 'github-main'
$existingFed = (Invoke-Az @('ad','app','federated-credential','list','--id',$ghAppId,'--query',"[?name=='$fedName'] | [0].name",'-o','tsv') -AllowFail | Out-String).Trim()
if (-not $existingFed) {
    $fed = @{
        name      = $fedName
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = "repo:${GitHubOrg}/${GitHubRepo}:ref:refs/heads/main"
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json -Compress
    Invoke-Az @('ad','app','federated-credential','create','--id',$ghAppId,'--parameters',$fed) | Out-Null
    Write-Host "    federated credential '$fedName' created"
} else { Write-Host "    federated credential '$fedName' present" }

# -- 2. Web / platform SPA app registration ------------------------------------
$webName = "$NamePrefix-platform"
$platformClientId = Get-AppId $webName
if (-not $platformClientId) {
    Write-Host "    creating $webName"
    $platformClientId = (Invoke-Az @('ad','app','create','--display-name',$webName,'--query','appId','-o','tsv') | Out-String).Trim()
} else { Write-Host "    reusing $webName ($platformClientId)" }
# SPA redirect URIs via Graph PATCH (reliable array handling; idempotent set)
$webObjId = (Invoke-Az @('ad','app','show','--id',$platformClientId,'--query','id','-o','tsv') | Out-String).Trim()
$spaBody = @{ spa = @{ redirectUris = @($WebUrl, "$WebUrl/") } } | ConvertTo-Json -Depth 5 -Compress
Invoke-Az @('rest','--method','PATCH','--uri',"https://graph.microsoft.com/v1.0/applications/$webObjId",'--headers','Content-Type=application/json','--body',$spaBody) -AllowFail | Out-Null

# -- 3. API app registration (exposes api://<id>/access_as_user) ---------------
$apiName = "$NamePrefix-agents"
$apiClientId = Get-AppId $apiName
if (-not $apiClientId) {
    Write-Host "    creating $apiName"
    $apiClientId = (Invoke-Az @('ad','app','create','--display-name',$apiName,'--query','appId','-o','tsv') | Out-String).Trim()
} else { Write-Host "    reusing $apiName ($apiClientId)" }
Invoke-Az @('ad','app','update','--id',$apiClientId,'--identifier-uris',"api://$apiClientId") -AllowFail | Out-Null

# Expose an access_as_user scope (best-effort; non-fatal if it already exists)
$apiObjId = (Invoke-Az @('ad','app','show','--id',$apiClientId,'--query','id','-o','tsv') | Out-String).Trim()
$scopeId  = [guid]::NewGuid().ToString()
$scopeBody = @{
    api = @{
        oauth2PermissionScopes = @(@{
            id = $scopeId; isEnabled = $true; type = 'User'; value = 'access_as_user'
            adminConsentDisplayName = 'Access TectikaAgents API'
            adminConsentDescription = 'Allow the app to access the TectikaAgents API on behalf of the signed-in user.'
        })
    }
} | ConvertTo-Json -Depth 6 -Compress
# Only set if no scope exists yet (avoid clobbering)
$hasScope = (Invoke-Az @('ad','app','show','--id',$apiClientId,'--query','length(api.oauth2PermissionScopes)','-o','tsv') -AllowFail | Out-String).Trim()
if ($hasScope -eq '0' -or [string]::IsNullOrEmpty($hasScope)) {
    Invoke-Az @('rest','--method','PATCH','--uri',"https://graph.microsoft.com/v1.0/applications/$apiObjId",'--headers','Content-Type=application/json','--body',$scopeBody) -AllowFail | Out-Null
}

# -- Output --------------------------------------------------------------------
$result = [ordered]@{
    githubAppId      = $ghAppId
    apiClientId      = $apiClientId
    platformClientId = $platformClientId
}
$result | ConvertTo-Json | Set-Content -Path $OutFile -Encoding utf8

Write-Host ""
Write-Host "==> Entra outputs (written to $OutFile):"
Write-Host "    githubAppId      = $ghAppId"
Write-Host "    apiClientId      = $apiClientId"
Write-Host "    platformClientId = $platformClientId"
