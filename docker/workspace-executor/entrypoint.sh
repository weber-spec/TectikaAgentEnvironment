#!/bin/bash
# Workspace container entrypoint.
#  - With REPO_URL set: configure git with the PAT, clone, check out GIT_BRANCH.
#  - Without REPO_URL: a bare, git-isolated /workspace (no clone, no git init).
# Then start the HTTP executor.
set -euo pipefail

mkdir -p /workspace

if [ -n "${REPO_URL:-}" ]; then
    echo "[entrypoint] repo=$REPO_URL branch=${GIT_BRANCH:-} can_push=${GIT_CAN_PUSH:-false}"
    git config --global credential.helper store
    printf "https://x-access-token:%s@github.com\n" "${GIT_PAT:-}" > /root/.git-credentials
    chmod 600 /root/.git-credentials
    git config --global user.email "agent@tectika.com"
    git config --global user.name "Tectika Agent"
    # Clone with retry/backoff — a transient GitHub/network blip (Octokit NotFound / SocketException seen in
    # QA S3 §4.2) must not crash the sandbox into a silent 300s health-timeout. Retry a few times, and only
    # fail hard (clear log) if the repo is genuinely unreachable.
    clone_ok=false
    for attempt in 1 2 3; do
        if git clone "$REPO_URL" /workspace; then clone_ok=true; break; fi
        echo "[entrypoint] clone attempt $attempt failed; retrying in $((attempt * 3))s"
        rm -rf /workspace && mkdir -p /workspace
        sleep "$((attempt * 3))"
    done
    if [ "$clone_ok" != true ]; then
        echo "[entrypoint] FATAL: git clone failed after 3 attempts for $REPO_URL"
        exit 1
    fi
    cd /workspace
    if git ls-remote --heads origin "${GIT_BRANCH:-}" | grep -q "${GIT_BRANCH:-}"; then
        git checkout "$GIT_BRANCH"
    else
        git checkout -b "$GIT_BRANCH"
    fi
    # Permission gate: roles without CanPushCode get a workspace that can read but not push. Point the
    # push URL at a dead address so a casual `git push` fails fast (defense-in-depth; the finalization
    # push is also gated server-side). Airtight read/write separation needs a read-only token (infra).
    if [ "${GIT_CAN_PUSH:-false}" != "true" ]; then
        git remote set-url --push origin "no-push://disabled" || true
        echo "[entrypoint] push disabled for this role"
    fi
    echo "[entrypoint] ready on branch $(git branch --show-current)"
else
    cd /workspace
    echo "[entrypoint] standalone sandbox (no repo) at /workspace"
fi

# Keep the raw token out of the executor's environment (and therefore out of every run_command shell):
# auth now lives only in /root/.git-credentials, which the credential helper uses for clone/push. This
# prevents `env`/process-inspection from surfacing the PAT; combined with output scrubbing on the host.
unset GIT_PAT

exec python3 /executor.py
