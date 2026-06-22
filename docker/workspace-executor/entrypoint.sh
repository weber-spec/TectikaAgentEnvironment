#!/bin/bash
# Workspace container entrypoint.
#  - With REPO_URL set: configure git with the PAT, clone, check out GIT_BRANCH.
#  - Without REPO_URL: a bare, git-isolated /workspace (no clone, no git init).
# Then start the HTTP executor.
set -euo pipefail

mkdir -p /workspace

if [ -n "${REPO_URL:-}" ]; then
    echo "[entrypoint] repo=$REPO_URL can_push=${GIT_CAN_PUSH:-false}"
    git config --global credential.helper store
    printf "https://x-access-token:%s@github.com\n" "${GIT_PAT:-}" > /root/.git-credentials
    chmod 600 /root/.git-credentials
    git config --global user.email "agent@tectika.com"
    git config --global user.name "Tectika Agent"
    git clone "$REPO_URL" /workspace
    cd /workspace
    # Permission gate: roles without CanPushCode get a workspace that can read but not push.
    if [ "${GIT_CAN_PUSH:-false}" != "true" ]; then
        git remote set-url --push origin "no-push://disabled" || true
        echo "[entrypoint] push disabled for this role"
    fi
    echo "[entrypoint] ready on $(git branch --show-current)"
else
    cd /workspace
    echo "[entrypoint] standalone sandbox (no repo) at /workspace"
fi

# Keep the raw token out of the executor's environment (and therefore out of every run_command shell):
# auth now lives only in /root/.git-credentials, which the credential helper uses for clone/push. This
# prevents `env`/process-inspection from surfacing the PAT; combined with output scrubbing on the host.
unset GIT_PAT

exec python3 /executor.py
