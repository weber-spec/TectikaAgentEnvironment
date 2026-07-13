<#
.SYNOPSIS
  dev-local.ps1 - run the Tectika stack LOCALLY against the REAL Azure resources
  (Windows PowerShell port of scripts/dev-local.sh).

.DESCRIPTION
  Starts (each in its own hidden cmd.exe, logging to .dev-local\logs\):
    - azurite    local Storage emulator: Durable Functions task hub  :10000
    - api        .NET API           http://localhost:5000  (dotnet watch = hot reload)
    - workflows  Durable Functions  http://localhost:7071  (func start)
    - web        Next.js dev server http://localhost:3000

  Run scripts/dev-local-setup.sh (under WSL/bash) ONCE first to generate the local
  config files - this script only launches the stack, it does not generate config.

.EXAMPLE
  .\scripts\dev-local.ps1 up        # start everything (idempotent - safe to re-run)
.EXAMPLE
  .\scripts\dev-local.ps1 down      # stop everything
.EXAMPLE
  .\scripts\dev-local.ps1 logs      # tail all logs
.EXAMPLE
  .\scripts\dev-local.ps1 status
.EXAMPLE
  .\scripts\dev-local.ps1 restart
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet('up', 'start', 'down', 'stop', 'logs', 'status', 'restart')]
  [string] $Command = 'up'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ROOT     = Split-Path -Parent $PSScriptRoot
$RUNDIR   = Join-Path $ROOT '.dev-local'
$LOGDIR   = Join-Path $RUNDIR 'logs'
$PIDDIR   = Join-Path $RUNDIR 'pids'
$BATDIR   = Join-Path $RUNDIR 'bat'
$API_PORT  = 5138   # matches launchSettings.json + web .env.local + workflows Api__BaseUrl
$FUNC_PORT = 7071
$WEB_PORT  = 3000
# Tenant that owns the Azure resources (cosmos-agentteam, Foundry, Key Vault). Pinned because
# DefaultAzureCredential walks a chain of sources (env -> broker/WAM cache -> VS -> Azure CLI) and,
# on a machine signed in to several tenants, an earlier source can hand back a token from the wrong
# one. Cosmos then rejects it with 401/5007 ("token issued by authority [...] which is not trusted"),
# non-deterministically between runs. AZURE_TENANT_ID constrains the whole chain to this tenant.
$TENANT_ID       = '134f5740-154b-4915-9e20-7f25e65d6edc'
$SUBSCRIPTION_ID = '929e4f09-f929-4ebe-b146-3723b1e283b5'   # "Visual Studio Enterprise - MPN", owns rg-agentteam-dev-001
New-Item -ItemType Directory -Force -Path $LOGDIR, $PIDDIR, $BATDIR | Out-Null

# --------------------------------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------------------------------

function Test-PortBusy([int] $Port) {
  [bool] (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

# Block until a port is listening (or timeout). Returns $true if it came up.
function Wait-Port([int] $Port, [int] $MaxWaitSec = 150) {
  for ($i = 0; $i -lt ($MaxWaitSec / 5); $i++) {
    if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { return $true }
    Start-Sleep 5
  }
  return $false
}

# Force-kill a process tree. Routed through cmd so taskkill's stderr (e.g. a stale pid that
# is already gone) is swallowed and never surfaces as a terminating NativeCommandError.
function Stop-Tree([int] $TreePid) {
  if ($TreePid) { & $env:ComSpec /c "taskkill /T /F /PID $TreePid >nul 2>&1" | Out-Null }
}

# Kill whatever currently listens on a TCP port (and its process tree), so a repeated
# `up` never leaves orphaned watchers behind. Targeted to our app ports only.
function Clear-Port([int] $Port) {
  $owners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique
  foreach ($owner in $owners) {
    if ($owner -and $owner -ne 0) { Stop-Tree $owner }
  }
}

# Stop instances we have pidfiles for (the recorded pid is the launcher cmd.exe; /T
# kills the whole tree - dotnet watch / func / next live underneath it).
function Stop-Tracked {
  Get-ChildItem -Path $PIDDIR -File -ErrorAction SilentlyContinue | ForEach-Object {
    $procId = (Get-Content $_.FullName -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($procId) { Stop-Tree ([int] $procId) }
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
  }
}

# Start-Svc <name> <workdir> <envHashtable> <command>
# Writes a wrapper .cmd (so env + cwd + merged log redirection are robust), launches it
# hidden, and records the launcher pid.
function Start-Svc {
  param(
    [string] $Name,
    [string] $WorkDir,
    [hashtable] $EnvVars,
    [string] $CommandLine
  )
  $log = Join-Path $LOGDIR "$Name.log"
  $bat = Join-Path $BATDIR "$Name.cmd"
  Set-Content -Path $log -Value '' -Encoding ASCII   # truncate previous run's log

  # Prepend the npm global dir so cmd.exe resolves npm-installed shims (func, azurite)
  # that otherwise live only as PowerShell (.ps1) shims off cmd's PATH.
  $lines = @('@echo off', 'set "PATH=%APPDATA%\npm;%PATH%"', "cd /d ""$WorkDir""")
  foreach ($k in $EnvVars.Keys) { $lines += "set ""$k=$($EnvVars[$k])""" }
  $lines += "$CommandLine >> ""$log"" 2>&1"
  Set-Content -Path $bat -Value $lines -Encoding ASCII

  $p = Start-Process -FilePath $env:ComSpec -ArgumentList '/c', $bat -WindowStyle Hidden -PassThru
  $p.Id | Set-Content -Path (Join-Path $PIDDIR $Name) -Encoding ASCII
  '   started {0,-9} log={1}' -f $Name, $log | Write-Host
}

# Fail fast if the Azure CLI cannot mint tokens for $TENANT_ID.
#
# AZURE_TENANT_ID (below) only tells DefaultAzureCredential WHICH tenant to ask for; it cannot make
# the signed-in az account a member of it. When az is left on a subscription from another tenant, the
# chain still reaches AzureCliCredential, which fails with AADSTS90072 ("user ... does not exist in
# tenant"). The failure is silent and PARTIAL: Cosmos keeps working (it falls back to a connection
# string), so boards and tasks load normally while every AAD-backed dependency is dead - Foundry
# (GET /api/models -> 502 "Could not load models from Foundry", agents cannot be provisioned or run),
# Key Vault, and Service Bus. That looks like a product bug, so check it up front instead.
#
# Every az call is routed through cmd (same trick as Stop-Tree): az writes its diagnostics to stderr,
# and under $ErrorActionPreference='Stop' PowerShell 5.1 turns those lines into a terminating
# NativeCommandError - which would abort with az's raw traceback instead of the guidance below.
function Invoke-Az([string] $Arguments) {
  & $env:ComSpec /c "az $Arguments >nul 2>&1"
  return $LASTEXITCODE
}
function Get-AzValue([string] $Arguments) {
  $out = & $env:ComSpec /c "az $Arguments 2>nul"
  return ($out | Select-Object -First 1)
}

function Assert-AzContext {
  if ((Invoke-Az 'account show') -ne 0) {
    Write-Error "not signed in to az - run: az login --tenant $TENANT_ID"
  }

  if ((Get-AzValue 'account show --query id -o tsv') -ne $SUBSCRIPTION_ID) {
    Write-Host "   az is on '$(Get-AzValue 'account show --query name -o tsv')' - switching to the project subscription"
    if ((Invoke-Az "account set --subscription $SUBSCRIPTION_ID") -ne 0) {
      Write-Error "az has no access to subscription $SUBSCRIPTION_ID - run: az login --tenant $TENANT_ID"
    }
  }

  # Selecting the subscription is not proof: az keeps entries for subscriptions the signed-in account
  # can no longer authenticate against. Only a real token request settles it. ai.azure.com is the
  # scope the Foundry model catalog uses, i.e. the first thing that breaks.
  if ((Invoke-Az "account get-access-token --tenant $TENANT_ID --scope https://ai.azure.com/.default") -ne 0) {
    $who = Get-AzValue 'account show --query user.name -o tsv'
    Write-Error @"
az is signed in as '$who', which cannot get a token in tenant $TENANT_ID.
Foundry, Key Vault and Service Bus would all fail while Cosmos kept working off its connection
string, so the stack would come up looking healthy and then break on the first agent run.
Fix it, then re-run:

    az login --tenant $TENANT_ID
"@
  }
  Write-Host "   az context OK (subscription $SUBSCRIPTION_ID, tenant $TENANT_ID)"
}

# --------------------------------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------------------------------

function Invoke-Up {
  if (-not (Test-Path (Join-Path $ROOT 'src\workflows\local.settings.json'))) {
    Write-Error 'Missing local config - run scripts/dev-local-setup.sh (under WSL) first.'
  }

  Assert-AzContext

  # Idempotent start: stop tracked instances and free the app ports first.
  Stop-Tracked
  foreach ($p in @($API_PORT, $FUNC_PORT, $WEB_PORT)) { Clear-Port $p }

  # Pre-build (sequential) BEFORE starting the watchers. The api (dotnet watch) and
  # workflows (func) builds both compile the shared net9.0 outputs of Core/AgentRuntime;
  # started together on a cold `up` they race on those DLLs -> CS2012 "used by another
  # process", which fails workflows' build and can make the API load a half-written
  # assembly (it then exits with no startup log). Building once up front leaves every
  # output current, so each watcher finds it up-to-date and skips the concurrent compile.
  Write-Host 'Pre-building .NET projects (avoids the concurrent-build file lock)...'
  $prebuildLog = Join-Path $LOGDIR 'prebuild.log'
  Set-Content -Path $prebuildLog -Value '' -Encoding ASCII
  foreach ($proj in @('src\api\TectikaAgents.Api\TectikaAgents.Api.csproj', 'src\workflows\TectikaAgents.Workflows.csproj')) {
    & dotnet build (Join-Path $ROOT $proj) -c Debug --nologo -v minimal *>> $prebuildLog
    if ($LASTEXITCODE -ne 0) { Write-Host "   [!!] pre-build of $proj reported errors (see .dev-local/logs/prebuild.log) - continuing" }
  }

  Write-Host 'Starting local stack (real Azure data + agents)...'

  if (Test-PortBusy 10000) {
    Write-Host '   azurite already running on :10000 (leaving it)'
  }
  else {
    Start-Svc 'azurite' $RUNDIR @{} "azurite --silent --location ""$RUNDIR\azurite"""
  }

  # --property:UseAppHost=false -> `dotnet run` execs the DLL through dotnet.exe instead of
  #   the per-app apphost .exe. On this machine the unsigned apphost in a user folder is
  #   blocked at process start ("Access is denied", AppLocker/AV); the DLL path is allowed.
  # DOTNET_USE_POLLING_FILE_WATCHER: the API's watcher also observes src/workflows; when `func`
  #   rebuilds there it floods the native FileSystemWatcher ("Too many changes at once") and the
  #   API restart-loops. Polling has no such buffer to overflow.
  Start-Svc 'api' (Join-Path $ROOT 'src\api\TectikaAgents.Api') @{
    ASPNETCORE_ENVIRONMENT          = 'Development'
    ASPNETCORE_URLS                 = "http://localhost:$API_PORT"
    DOTNET_USE_POLLING_FILE_WATCHER = 'true'
    AZURE_TENANT_ID                 = $TENANT_ID
  } 'dotnet watch --non-interactive run --no-launch-profile --property:UseAppHost=false'

  # web is Node/Next - it never touches the .NET build, so start it now to compile in parallel.
  Start-Svc 'web' (Join-Path $ROOT 'src\web\tectika-board') @{} 'npm run dev'

  # Stagger workflows AFTER the API is listening. `func start` recompiles the shared net9.0
  # projects into its own output dir; started alongside the API watcher's build it races on
  # the same obj DLLs (CS2012) and the func host dies. Waiting removes the overlap. (The
  # pre-build above covers the API; func rebuilds regardless, so it needs the API done first.)
  Write-Host '   waiting for the API to bind before starting workflows (avoids the build race)...'
  if (-not (Wait-Port $API_PORT 180)) { Write-Host '   [!!] API did not come up in time - starting workflows anyway' }

  Start-Svc 'workflows' (Join-Path $ROOT 'src\workflows') @{
    DOTNET_ROLL_FORWARD = 'Major'
    AZURE_TENANT_ID     = $TENANT_ID
  } "func start --port $FUNC_PORT"

  @"

Stack starting (first build of the .NET projects can take ~30-60s):
   Web        http://localhost:$WEB_PORT
   API        http://localhost:$API_PORT   (OpenAPI at /openapi/v1.json)
   Workflows  http://localhost:$FUNC_PORT

   Follow logs:  .\scripts\dev-local.ps1 logs
   Stop:         .\scripts\dev-local.ps1 down
"@ | Write-Host
}

function Invoke-Down {
  $had = [bool] (Get-ChildItem -Path $PIDDIR -File -ErrorAction SilentlyContinue)
  Stop-Tracked
  foreach ($p in @($API_PORT, $FUNC_PORT, $WEB_PORT, 10000)) { Clear-Port $p }
  if ($had) { Write-Host '   stopped local stack' } else { Write-Host '   stopped (swept ports)' }
}

function Invoke-Status {
  $any = $false
  Get-ChildItem -Path $PIDDIR -File -ErrorAction SilentlyContinue | ForEach-Object {
    $any = $true
    $name = $_.Name
    $procId = (Get-Content $_.FullName -ErrorAction SilentlyContinue | Select-Object -First 1)
    $alive = $procId -and (Get-Process -Id $procId -ErrorAction SilentlyContinue)
    if ($alive) { Write-Host "   ${name}: up (pid $procId)" } else { Write-Host "   ${name}: down" }
  }
  if (-not $any) { Write-Host 'not running' }
}

# tail -F across all logs, prefixing each line with the service name.
function Invoke-Logs {
  Write-Host 'Tailing logs (Ctrl-C to stop)...'
  $pos = @{}
  while ($true) {
    foreach ($f in (Get-ChildItem -Path $LOGDIR -Filter *.log -ErrorAction SilentlyContinue)) {
      $path = $f.FullName
      if (-not $pos.ContainsKey($path)) { $pos[$path] = [int64] 0 }
      try {
        $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
      }
      catch { continue }
      try {
        if ($fs.Length -lt $pos[$path]) { $pos[$path] = [int64] 0 }   # log was truncated (restart)
        if ($fs.Length -gt $pos[$path]) {
          [void] $fs.Seek($pos[$path], 'Begin')
          $sr = New-Object System.IO.StreamReader($fs)
          $text = $sr.ReadToEnd()
          $pos[$path] = $fs.Position
          $sr.Dispose()
          foreach ($line in ($text -split "`n")) {
            $line = $line.TrimEnd("`r")
            if ($line -ne '') { Write-Host ('[{0}] {1}' -f $f.BaseName, $line) }
          }
        }
      }
      finally { $fs.Dispose() }
    }
    Start-Sleep -Milliseconds 500
  }
}

switch ($Command) {
  { $_ -in 'up', 'start' }  { Invoke-Up }
  { $_ -in 'down', 'stop' } { Invoke-Down }
  'logs'    { Invoke-Logs }
  'status'  { Invoke-Status }
  'restart' { Invoke-Down; Invoke-Up }
}
