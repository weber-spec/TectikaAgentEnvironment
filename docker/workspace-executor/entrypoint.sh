#!/bin/bash
# Workspace container entrypoint.
#  - With REPO_URL set: configure git with the PAT, clone, check out GIT_BRANCH.
#  - Without REPO_URL: a bare, git-isolated /workspace (no clone, no git init).
# Then start the HTTP executor.
set -euo pipefail

mkdir -p /workspace

if [ -n "${REPO_URL:-}" ]; then
    echo "[entrypoint] repo=$REPO_URL branch=${GIT_BRANCH:-}"
    git config --global credential.helper store
    printf "https://x-access-token:%s@github.com\n" "${GIT_PAT:-}" > /root/.git-credentials
    git config --global user.email "agent@tectika.com"
    git config --global user.name "Tectika Agent"
    git clone "$REPO_URL" /workspace
    cd /workspace
    if git ls-remote --heads origin "${GIT_BRANCH:-}" | grep -q "${GIT_BRANCH:-}"; then
        git checkout "$GIT_BRANCH"
    else
        git checkout -b "$GIT_BRANCH"
    fi
    echo "[entrypoint] ready on branch $(git branch --show-current)"
else
    cd /workspace
    echo "[entrypoint] standalone sandbox (no repo) at /workspace"
fi

exec python3 /executor.py
