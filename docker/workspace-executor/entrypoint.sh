#!/bin/bash
# Workspace container entrypoint (board-level, worktree architecture).
#  - With REPO_URL set: configure git with the PAT, clone to /workspace/main.
#    Individual runs get isolated git worktrees at /workspace/runs/{run_id} via POST /worktree/add.
#  - Without REPO_URL: a bare /workspace/main (no clone, no git init).
# Then start the HTTP executor.
set -euo pipefail

mkdir -p /workspace/main /workspace/runs

if [ -n "${REPO_URL:-}" ]; then
    echo "[entrypoint] repo=$REPO_URL can_push=${GIT_CAN_PUSH:-false}"
    git config --global credential.helper store
    printf "https://x-access-token:%s@github.com\n" "${GIT_PAT:-}" > /root/.git-credentials
    chmod 600 /root/.git-credentials
    git config --global user.email "agent@tectika.com"
    git config --global user.name "Tectika Agent"
    # Clone to /workspace/main with retry/backoff — no branch checkout; worktrees are created per-run by the
    # backend. A transient GitHub/network blip (Octokit NotFound / SocketException seen in QA S3 §4.2) must not
    # crash the sandbox into a silent 300s health-timeout, so retry a few times and only fail hard (clear log)
    # if the repo is genuinely unreachable.
    clone_ok=false
    for attempt in 1 2 3; do
        if git clone --depth=1 "$REPO_URL" /workspace/main; then clone_ok=true; break; fi
        echo "[entrypoint] clone attempt $attempt failed; retrying in $((attempt * 3))s"
        rm -rf /workspace/main
        sleep "$((attempt * 3))"
    done
    if [ "$clone_ok" != true ]; then
        echo "[entrypoint] FATAL: git clone failed after 3 attempts for $REPO_URL"
        exit 1
    fi
    cd /workspace/main
    # Permission gate: roles without CanPushCode get a workspace that can read but not push.
    if [ "${GIT_CAN_PUSH:-false}" != "true" ]; then
        git remote set-url --push origin "no-push://disabled" || true
        echo "[entrypoint] push disabled for this role"
    fi
    echo "[entrypoint] ready on $(git branch --show-current) at /workspace/main"
else
    echo "[entrypoint] standalone sandbox (no repo) at /workspace/main"
fi

# Keep the raw token out of the executor's environment (and therefore out of every run_command shell):
# auth now lives only in /root/.git-credentials, which the credential helper uses for clone/push. This
# prevents `env`/process-inspection from surfacing the PAT; combined with output scrubbing on the host.
unset GIT_PAT

exec python3 /executor.py
