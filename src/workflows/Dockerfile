# Build context: repo root
# docker build -f src/workflows/Dockerfile -t tectikaagents-workflows .

# ── Stage 1: restore ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY src/core/TectikaAgents.Core/TectikaAgents.Core.csproj src/core/TectikaAgents.Core/
COPY src/workflows/TectikaAgents.Workflows.csproj           src/workflows/

RUN dotnet restore src/workflows/TectikaAgents.Workflows.csproj

# ── Stage 2: publish ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
COPY src/core/      src/core/
COPY src/workflows/ src/workflows/

RUN dotnet publish src/workflows/TectikaAgents.Workflows.csproj \
    -c Release -o /app/publish --no-restore

# ── Stage 3: runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated9.0 AS runtime
WORKDIR /home/site/wwwroot

COPY --from=publish /app/publish .

ENV AzureWebJobsScriptRoot=/home/site/wwwroot
ENV AzureFunctionsJobHost__Logging__Console__IsEnabled=true
ENV FUNCTIONS_WORKER_RUNTIME=dotnet-isolated

EXPOSE 80
