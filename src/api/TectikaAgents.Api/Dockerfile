# Multi-stage build for the Tectika API (.NET 10). Build context = repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore (copy csproj first for layer caching). The API references Core only.
COPY src/core/TectikaAgents.Core/TectikaAgents.Core.csproj src/core/TectikaAgents.Core/
COPY src/api/TectikaAgents.Api/TectikaAgents.Api.csproj src/api/TectikaAgents.Api/
RUN dotnet restore src/api/TectikaAgents.Api/TectikaAgents.Api.csproj

# Copy sources and publish.
COPY src/core/TectikaAgents.Core/ src/core/TectikaAgents.Core/
COPY src/api/TectikaAgents.Api/ src/api/TectikaAgents.Api/
RUN dotnet publish src/api/TectikaAgents.Api/TectikaAgents.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Container Apps ingress targets port 8080 (the aspnet image default).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TectikaAgents.Api.dll"]
