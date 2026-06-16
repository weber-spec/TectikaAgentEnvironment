# ── Agent workspace image ──────────────────────────────────────────────────────
# Base: official .NET SDK image (Debian-based) so .NET is always present.
# Adds: git · Python 3 · Node.js 20 · pip
# Entry: entrypoint.sh → clones repo → starts executor.py on :8080
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

# ── Executor ───────────────────────────────────────────────────────────────────
COPY executor.py /executor.py
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 8080
ENTRYPOINT ["/entrypoint.sh"]
