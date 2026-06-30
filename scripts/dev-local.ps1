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
New-Item -ItemType Directory -Force -Path $LOGDIR, $PIDDIR, $BATDIR | Out-Null

# --------------------------------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------------------------------

function Test-PortBusy([int] $Port) {
  [bool] (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
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

# --------------------------------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------------------------------

function Invoke-Up {
  if (-not (Test-Path (Join-Path $ROOT 'src\workflows\local.settings.json'))) {
    Write-Error 'Missing local config - run scripts/dev-local-setup.sh (under WSL) first.'
  }

  # Idempotent start: stop tracked instances and free the app ports first.
  Stop-Tracked
  foreach ($p in @($API_PORT, $FUNC_PORT, $WEB_PORT)) { Clear-Port $p }
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
  } 'dotnet watch --non-interactive run --no-launch-profile --property:UseAppHost=false'

  Start-Svc 'workflows' (Join-Path $ROOT 'src\workflows') @{
    DOTNET_ROLL_FORWARD = 'Major'
  } "func start --port $FUNC_PORT"

  Start-Svc 'web' (Join-Path $ROOT 'src\web\tectika-board') @{} 'npm run dev'

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
