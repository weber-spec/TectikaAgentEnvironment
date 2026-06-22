#!/usr/bin/env bash
set -euo pipefail

: "${REPO_URL:?REPO_URL required}"
: "${GIT_BRANCH:?GIT_BRANCH required}"

git config --global credential.helper store
printf "https://x-access-token:%s@github.com\n" "${GIT_PAT:-}" > /root/.git-credentials
git config --global user.email "preview@tectika.com"
git config --global user.name "Tectika Preview"

git clone "$REPO_URL" /app
cd /app
git checkout "$GIT_BRANCH"

export PORT=8080 HOST=0.0.0.0 HOSTNAME=0.0.0.0

# Install with the lockfile's package manager.
if [ -f pnpm-lock.yaml ]; then corepack enable && pnpm install --frozen-lockfile;
elif [ -f yarn.lock ]; then corepack enable && yarn install --frozen-lockfile;
else npm install; fi

# Prefer a dev script; fall back to start. Fail loudly if neither exists.
if npm run | grep -qE '^  dev'; then exec npm run dev;
elif npm run | grep -qE '^  start'; then exec npm start;
else echo "No 'dev' or 'start' script in package.json - not previewable" >&2; exit 1; fi
