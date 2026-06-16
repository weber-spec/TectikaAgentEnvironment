#!/bin/bash
# Workspace container entrypoint.
# 1. Configure git with the PAT.
# 2. Clone REPO_URL into /workspace.
# 3. Check out GIT_BRANCH (create if new).
# 4. Start the HTTP executor.
set -euo pipefail

echo "[entrypoint] repo=$REPO_URL branch=$GIT_BRANCH"

# Embed PAT in the credential helper (avoids leaking it in the URL).
git config --global credential.helper store
printf "https://x-access-token:%s@github.com\n" "$GIT_PAT" > /root/.git-credentials

git config --global user.email "agent@tectika.com"
git config --global user.name "Tectika Agent"

# Clone
git clone "$REPO_URL" /workspace
cd /workspace

# Check out branch — create from HEAD if it doesn't exist yet on origin.
if git ls-remote --heads origin "$GIT_BRANCH" | grep -q "$GIT_BRANCH"; then
    git checkout "$GIT_BRANCH"
else
    git checkout -b "$GIT_BRANCH"
fi

echo "[entrypoint] ready on branch $(git branch --show-current)"
exec python3 /executor.py
