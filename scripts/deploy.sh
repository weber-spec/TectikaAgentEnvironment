#!/usr/bin/env bash
#
# deploy.sh - Deploy TectikaAgents surfaces (api / web / workflows) from this WSL/Linux box.
#
#   api / web   -> built in the cloud with `az acr build` (no local Docker) and rolled out
#                  to their Azure Container Apps via `az containerapp update`.
#   workflows   -> published to the Flex Consumption Function App with Azure Functions Core Tools.
#
# Images are tagged with the short git SHA (and :latest) so every rollout is traceable to a commit.
#
# Usage:
#   scripts/deploy.sh [--api] [--web] [--workflows] [--all] [--no-verify] [--allow-dirty] [-h]
#
# Resource names are hardcoded to the live `agentteam` tenant but every one can be overridden
# with a TECTIKA_* env var (see the Configuration block) so the script is tenant-portable.
#
set -euo pipefail

# --------------------------------------------------------------------------------------------------
# Configuration (hardcoded defaults, each overridable via env var)
# --------------------------------------------------------------------------------------------------
SUBSCRIPTION_ID="${TECTIKA_SUBSCRIPTION_ID:-929e4f09-f929-4ebe-b146-3723b1e283b5}"
RESOURCE_GROUP="${TECTIKA_RESOURCE_GROUP:-rg-agentteam-dev-001}"
ACR_NAME="${TECTIKA_ACR_NAME:-tacragentteam}"
ACR_LOGIN_SERVER="${TECTIKA_ACR_LOGIN_SERVER:-${ACR_NAME}.azurecr.io}"
ACA_DOMAIN="${TECTIKA_ACA_DOMAIN:-calmstone-c10c7a54.westeurope.azurecontainerapps.io}"

API_APP="${TECTIKA_API_APP:-ca-agentteam-api}"
WEB_APP="${TECTIKA_WEB_APP:-ca-agentteam-web}"
FUNC_APP="${TECTIKA_FUNC_APP:-func-agentteam-workflows}"

API_IMAGE="${TECTIKA_API_IMAGE:-agentteam-api}"
WEB_IMAGE="${TECTIKA_WEB_IMAGE:-agentteam-web}"
PREVIEW_IMAGE="${TECTIKA_PREVIEW_IMAGE:-preview-runner}"

API_FQDN="https://${API_APP}.${ACA_DOMAIN}"
WEB_FQDN="https://${WEB_APP}.${ACA_DOMAIN}"

# Health/smoke verification: how long to wait for a fresh revision to go Healthy/Running.
# ACA cold starts (image pull + app startup) can take a few minutes, so keep this generous.
VERIFY_TIMEOUT_SECONDS=300
VERIFY_POLL_SECONDS=5

# --------------------------------------------------------------------------------------------------
# Logging helpers (ASCII only)
# --------------------------------------------------------------------------------------------------
log()  { printf '\n>> %s\n' "$*"; }
info() { printf '   %s\n' "$*"; }
warn() { printf '!! %s\n' "$*" >&2; }
die()  { printf '\nERROR: %s\n' "$*" >&2; exit 1; }

# --------------------------------------------------------------------------------------------------
# Usage
# --------------------------------------------------------------------------------------------------
usage() {
  cat <<EOF
Deploy TectikaAgents surfaces to Azure.

Usage:
  scripts/deploy.sh [surfaces] [options]

Surfaces (pick at least one):
  --api          Deploy the API container app ($API_APP)
  --web          Deploy the Web container app ($WEB_APP)
  --workflows    Deploy the Functions app ($FUNC_APP)
  --all          Deploy all three (api, web, workflows)

Options:
  --no-verify    Skip post-deploy health + smoke checks
  --allow-dirty  Allow deploying with an uncommitted working tree (tags are SHA-based)
  -h, --help     Show this help

Examples:
  scripts/deploy.sh --api
  scripts/deploy.sh --web --workflows
  scripts/deploy.sh --all
EOF
}

# --------------------------------------------------------------------------------------------------
# Argument parsing
# --------------------------------------------------------------------------------------------------
DO_API=false
DO_WEB=false
DO_WORKFLOWS=false
VERIFY=true
ALLOW_DIRTY=false

[ $# -eq 0 ] && { usage; exit 2; }

while [ $# -gt 0 ]; do
  case "$1" in
    --api)         DO_API=true ;;
    --web)         DO_WEB=true ;;
    --workflows)   DO_WORKFLOWS=true ;;
    --all)         DO_API=true; DO_WEB=true; DO_WORKFLOWS=true ;;
    --no-verify)   VERIFY=false ;;
    --allow-dirty) ALLOW_DIRTY=true ;;
    -h|--help)     usage; exit 0 ;;
    *)             usage; die "Unknown argument: $1" ;;
  esac
  shift
done

if ! $DO_API && ! $DO_WEB && ! $DO_WORKFLOWS; then
  usage
  die "No surface selected. Pass --api, --web, --workflows, or --all."
fi

# --------------------------------------------------------------------------------------------------
# Prerequisite checks (fail fast, before any build)
# --------------------------------------------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

check_prereqs() {
  log "Checking prerequisites"

  command -v git >/dev/null 2>&1 || die "git not found on PATH."
  command -v az  >/dev/null 2>&1 || die "Azure CLI ('az') not found on PATH. Install the Linux az inside WSL."
  if $DO_WORKFLOWS; then
    command -v func >/dev/null 2>&1 || die "Azure Functions Core Tools ('func') not found on PATH (needed for --workflows)."
  fi

  # Must be logged in.
  local current_sub
  current_sub="$(az account show --query id -o tsv 2>/dev/null)" \
    || die "Not logged in to Azure. Run: az login"

  if [ "$current_sub" != "$SUBSCRIPTION_ID" ]; then
    warn "Active subscription is '$current_sub', expected '$SUBSCRIPTION_ID'."
    warn "Switch with: az account set --subscription $SUBSCRIPTION_ID"
    die "Wrong subscription. Aborting to avoid deploying to the wrong tenant."
  fi
  info "Azure subscription OK ($SUBSCRIPTION_ID)"
}

# --------------------------------------------------------------------------------------------------
# Git SHA / dirty-tree guard
# --------------------------------------------------------------------------------------------------
resolve_sha() {
  SHA="$(git rev-parse --short HEAD)"
  # Block on uncommitted changes to TRACKED files (staged or unstaged) so :$SHA is traceable.
  # Untracked files (e.g. .claude/, local notes) are not committed code, so they only warn.
  if ! git diff --quiet || ! git diff --cached --quiet; then
    if $ALLOW_DIRTY; then
      warn "Working tree has uncommitted changes; image tag :$SHA will NOT reflect them (--allow-dirty set)."
    else
      die "Working tree has uncommitted changes to tracked files. Commit them (so :$SHA is traceable) or pass --allow-dirty."
    fi
  fi
  if [ -n "$(git ls-files --others --exclude-standard)" ]; then
    warn "Untracked files present; they are not part of commit $SHA. Proceeding."
  fi
  log "Deploying from commit $SHA"
}

# --------------------------------------------------------------------------------------------------
# Verification helpers
# --------------------------------------------------------------------------------------------------
# Poll the revision running the image we just deployed (image tag == $SHA) until Healthy/Running,
# or time out. Targeting our own SHA avoids matching a draining old revision during the rollout.
wait_for_revision() {
  local app="$1"
  local deadline=$(( SECONDS + VERIFY_TIMEOUT_SECONDS ))
  info "Waiting for the :$SHA revision of $app to go Healthy/Running (timeout ${VERIFY_TIMEOUT_SECONDS}s)..."
  while [ "$SECONDS" -lt "$deadline" ]; do
    # -o tsv prints the two-element [healthState, runningState] array on two separate lines,
    # so read them line by line (line 1 = health, line 2 = running).
    local state health running
    state="$(az containerapp revision list -n "$app" -g "$RESOURCE_GROUP" \
      --query "[?ends_with(properties.template.containers[0].image, ':${SHA}')] | sort_by([],&properties.createdTime)[-1].[properties.healthState,properties.runningState]" \
      -o tsv 2>/dev/null || true)"
    health="$(printf '%s\n' "$state" | sed -n '1p')"
    running="$(printf '%s\n' "$state" | sed -n '2p')"
    if [ "$health" = "Healthy" ] && [ "$running" = "Running" ]; then
      info "Revision is Healthy/Running."
      return 0
    fi
    sleep "$VERIFY_POLL_SECONDS"
  done
  die "$app :$SHA did not reach Healthy/Running within ${VERIFY_TIMEOUT_SECONDS}s. Check: az containerapp revision list -n $app -g $RESOURCE_GROUP"
}

# Curl a URL and assert HTTP 200.
smoke_http() {
  local url="$1"
  local code
  code="$(curl -fsS -o /dev/null -w '%{http_code}' --max-time 30 "$url" 2>/dev/null || true)"
  if [ "$code" = "200" ]; then
    info "Smoke OK: $url -> 200"
  else
    die "Smoke FAILED: $url -> ${code:-no response}"
  fi
}

# --------------------------------------------------------------------------------------------------
# Surface deployers
# --------------------------------------------------------------------------------------------------
deploy_api() {
  log "Deploying API -> $API_APP"
  # Build context is the repo root; .dockerignore excludes src/web. The API image also pulls in
  # src/agentruntime (referenced by the Foundry runtime), so the root context is required.
  az acr build -r "$ACR_NAME" \
    -t "${API_IMAGE}:${SHA}" -t "${API_IMAGE}:latest" \
    -f src/api/Dockerfile .
  az containerapp update -n "$API_APP" -g "$RESOURCE_GROUP" \
    --image "${ACR_LOGIN_SERVER}/${API_IMAGE}:${SHA}" >/dev/null
  info "API image ${API_IMAGE}:${SHA} rolled out."

  # The preview-runner image is pulled by per-preview ACI containers at runtime (via the API's
  # Preview:AcrImage setting), not deployed as a container app, so it only needs to exist in ACR.
  # Building it here keeps it in lockstep with the API that provisions it -- no containerapp update.
  log "Building preview-runner image"
  az acr build -r "$ACR_NAME" \
    -t "${PREVIEW_IMAGE}:${SHA}" -t "${PREVIEW_IMAGE}:latest" \
    -f docker/preview-runner/Dockerfile docker/preview-runner/
  info "preview-runner image ${PREVIEW_IMAGE}:${SHA} pushed."

  if $VERIFY; then
    wait_for_revision "$API_APP"
    smoke_http "${API_FQDN}/api/boards"
  fi
}

deploy_web() {
  log "Deploying Web -> $WEB_APP"
  # Context (positional, last) is the Next.js app dir. NEXT_PUBLIC_API_URL is baked into the client
  # bundle at build time and must point at the live API. (--build-arg is singular.)
  az acr build -r "$ACR_NAME" \
    -t "${WEB_IMAGE}:${SHA}" -t "${WEB_IMAGE}:latest" \
    -f src/web/tectika-board/Dockerfile \
    --build-arg "NEXT_PUBLIC_API_URL=${API_FQDN}" \
    src/web/tectika-board/
  az containerapp update -n "$WEB_APP" -g "$RESOURCE_GROUP" \
    --image "${ACR_LOGIN_SERVER}/${WEB_IMAGE}:${SHA}" >/dev/null
  info "Web image ${WEB_IMAGE}:${SHA} rolled out."

  if $VERIFY; then
    wait_for_revision "$WEB_APP"
    smoke_http "${WEB_FQDN}/boards"
  fi
}

deploy_workflows() {
  log "Deploying Workflows -> $FUNC_APP"
  # Flex Consumption has no Kudu/SCM site, so zip-deploy fails. Core Tools is the supported path.
  # DOTNET_ROLL_FORWARD lets the host build the net9.0 project with a newer local SDK if needed.
  (
    cd src/workflows
    DOTNET_ROLL_FORWARD=LatestMajor func azure functionapp publish "$FUNC_APP" --dotnet-isolated
  ) || die "func publish failed for $FUNC_APP."
  info "Workflows published to $FUNC_APP."
  # func exits non-zero on failure (caught above) and prints 'Host ... Running' + the function list
  # on success, so a zero exit is the verification signal here.
}

# --------------------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------------------
check_prereqs
resolve_sha

# Deterministic order: api -> web -> workflows (web bundle targets the API FQDN).
$DO_API       && deploy_api
$DO_WEB       && deploy_web
$DO_WORKFLOWS && deploy_workflows

log "Deploy complete (commit $SHA)."
