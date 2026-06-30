#!/usr/bin/env bash
# =============================================================================
# dev-local.sh — run the Tectika stack LOCALLY against the REAL Azure resources.
#
# Starts (each in its own process group, logging to .dev-local/logs/):
#   - azurite    local Storage emulator: Durable Functions task hub
#   - api        .NET API           http://localhost:5000  (dotnet watch = hot reload)
#   - workflows  Durable Functions  http://localhost:7071  (func start)
#   - web        Next.js dev server http://localhost:3000
#
# Run scripts/dev-local-setup.sh ONCE first to generate the local config files.
#
# Usage:
#   scripts/dev-local.sh up        # start everything (idempotent — safe to re-run)
#   scripts/dev-local.sh down      # stop everything
#   scripts/dev-local.sh logs      # tail -F all logs
#   scripts/dev-local.sh status    # show what is up
#   scripts/dev-local.sh restart
#
# For active development you may prefer a terminal per service (clearer logs,
# Ctrl-C to restart one) — see docs/local-dev.md.
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNDIR="$ROOT/.dev-local"
LOGDIR="$RUNDIR/logs"
PIDDIR="$RUNDIR/pids"
API_PORT=5000
FUNC_PORT=7071
WEB_PORT=3000
mkdir -p "$LOGDIR" "$PIDDIR"

port_busy() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && { exec 3>&- 3<&-; return 0; }; return 1; }

# Kill whatever currently listens on a TCP port — by session, so children die too.
# Targeted (only our app ports), so it also reaps orphans a previous run leaked.
free_port() {
  local p="$1" pid sid
  for pid in $(ss -ltnp 2>/dev/null | grep -E ":$p\b" | grep -oP 'pid=\K[0-9]+' | sort -u); do
    sid=$(ps -o sid= -p "$pid" 2>/dev/null | tr -d ' ')
    [ -n "$sid" ] && pkill -KILL -s "$sid" 2>/dev/null
    kill -KILL "$pid" 2>/dev/null
  done
}

# Stop instances we have pidfiles for. The pid is a setsid session leader, so we
# signal the whole session (dotnet watch / func / next fork into their own groups).
stop_tracked() {
  local pf pid
  for pf in "$PIDDIR"/*; do
    [ -e "$pf" ] || continue
    pid="$(cat "$pf" 2>/dev/null)"
    if [ -n "$pid" ]; then
      pkill -TERM -s "$pid" 2>/dev/null; kill -TERM "$pid" 2>/dev/null
      pkill -KILL -s "$pid" 2>/dev/null; kill -KILL "$pid" 2>/dev/null
    fi
    rm -f "$pf"
  done
}

# spawn <name> <workdir> <command> — child records its OWN (session-leader) pid.
spawn() {
  local name="$1" workdir="$2" cmd="$3" log="$LOGDIR/$1.log"
  setsid bash -c "cd '$workdir' && echo \$\$ > '$PIDDIR/$name' && exec $cmd" >"$log" 2>&1 &
  printf '   started %-9s log=%s\n' "$name" "$log"
}

up() {
  [ -f "$ROOT/src/workflows/local.settings.json" ] || { echo "Missing local config — run scripts/dev-local-setup.sh first." >&2; exit 1; }
  # Idempotent start: stop tracked instances and free the app ports first, so a
  # repeated `up` never leaves orphaned file-watchers. (Orphans pile up and
  # exhaust the WSL inotify-instance limit, which crashes dotnet watch.)
  stop_tracked
  for p in "$API_PORT" "$FUNC_PORT" "$WEB_PORT"; do free_port "$p"; done
  echo "Starting local stack (real Azure data + agents)..."

  if port_busy 10000; then echo "   azurite already running on :10000 (leaving it)"; else
    spawn azurite "$RUNDIR" "azurite --silent --location '$RUNDIR/azurite'"
  fi
  # Bind 0.0.0.0, not localhost: on WSL2 a loopback-only bind isn't reachable from
  #   the Windows-side browser (next/func already bind all interfaces).
  # DOTNET_USE_POLLING_FILE_WATCHER: poll instead of inotify — WSL shares a small
  #   inotify-instance limit with VS Code; dotnet watch otherwise exhausts it.
  spawn api       "$ROOT/src/api/TectikaAgents.Api" "env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:$API_PORT DOTNET_USE_POLLING_FILE_WATCHER=true dotnet watch --non-interactive run --no-launch-profile"
  spawn workflows "$ROOT/src/workflows"             "env DOTNET_ROLL_FORWARD=Major func start --port $FUNC_PORT"
  spawn web       "$ROOT/src/web/tectika-board"     "npm run dev"

  cat <<EOF

Stack starting (first build of the .NET projects can take ~30-60s):
   Web        http://localhost:$WEB_PORT
   API        http://localhost:$API_PORT   (OpenAPI at /openapi/v1.json)
   Workflows  http://localhost:$FUNC_PORT

   Follow logs:  scripts/dev-local.sh logs
   Stop:         scripts/dev-local.sh down
EOF
}

down() {
  local had=0
  [ -n "$(ls -A "$PIDDIR" 2>/dev/null)" ] && had=1
  stop_tracked
  for p in "$API_PORT" "$FUNC_PORT" "$WEB_PORT" 10000; do free_port "$p"; done
  [ "$had" = 1 ] && echo "   stopped local stack" || echo "   stopped (swept ports)"
}

status() {
  local any=0 pf name pid
  for pf in "$PIDDIR"/*; do
    [ -e "$pf" ] || continue
    any=1; name="$(basename "$pf")"; pid="$(cat "$pf" 2>/dev/null)"
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then echo "   $name: up (pid $pid)"; else echo "   $name: down"; fi
  done
  [ "$any" = 1 ] || echo "not running"
}

case "${1:-up}" in
  up|start)  up ;;
  down|stop) down ;;
  logs)      tail -n +1 -F "$LOGDIR"/*.log ;;
  status)    status ;;
  restart)   down; up ;;
  *) echo "usage: $0 {up|down|logs|status|restart}"; exit 1 ;;
esac
