# Build context: repo root
# docker build -f src/api/Dockerfile -t tectikaagents-api .

# ── Stage 1: restore ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY src/core/TectikaAgents.Core/TectikaAgents.Core.csproj src/core/TectikaAgents.Core/
COPY src/api/TectikaAgents.Api/TectikaAgents.Api.csproj    src/api/TectikaAgents.Api/

RUN dotnet restore src/api/TectikaAgents.Api/TectikaAgents.Api.csproj

# ── Stage 2: publish ──────────────────────────────────────────────────────────
FROM restore AS publish
COPY src/core/ src/core/
COPY src/api/  src/api/

RUN dotnet publish src/api/TectikaAgents.Api/TectikaAgents.Api.csproj \
    -c Release -o /app/publish --no-restore

# ── Stage 3: runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN useradd -m appuser
USER appuser

COPY --from=publish /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080
ENTRYPOINT ["dotnet", "TectikaAgents.Api.dll"]
