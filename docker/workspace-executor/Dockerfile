# ── Agent workspace image ──────────────────────────────────────────────────────
# Base: official .NET SDK image (Debian-based) so .NET is always present.
# Adds: git · Python 3 · Node.js 20 · pip · Claude Code CLI
# Entry: entrypoint.sh → (clones repo if REPO_URL set) → starts executor.py on :8080
# ──────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0

ENV DEBIAN_FRONTEND=noninteractive

# ── Extra tools: git, Python 3, pip, jq, Node.js 20 ───────────────────────────
RUN apt-get update && apt-get install -y --no-install-recommends \
        git python3 python3-pip jq curl unzip \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# ── Claude Code CLI (the "ClaudeCode" execution engine) ───────────────────────
# Pinned for reproducible images; bump deliberately. Runs unattended via `claude -p`
# with --dangerously-skip-permissions inside this isolated, disposable per-board sandbox.
RUN npm install -g @anthropic-ai/claude-code@2.1.181

# Unattended defaults (no telemetry, no auto-update at run time). No secrets baked in —
# the Anthropic API key is delivered per-invocation via the job env, never in the image.
ENV CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    DISABLE_AUTOUPDATER=1 \
    DISABLE_TELEMETRY=1

# ── Executor ───────────────────────────────────────────────────────────────────
COPY executor.py /executor.py
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
